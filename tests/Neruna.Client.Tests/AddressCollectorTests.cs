using Microsoft.Extensions.DependencyInjection;
using MimeKit;
using Neruna.Core.Accounts;
using Neruna.Core.Contacts;
using Neruna.Core.Mail;
using Neruna.Core.Providers;
using Neruna.Providers.Special;

namespace Neruna.Client.Tests;

/// <summary>"Verwendete Adressen": recipients without a contact are kept on this device and suggested next time.</summary>
public class AddressCollectorTests
{
    [Fact]
    public async Task New_recipients_are_kept_once_and_leave_when_they_become_real_contacts()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var env = await TestEnvironment.CreateAsync(services =>
        {
            services.AddSingleton<IProviderFactory<IContactProvider>, LocalAddressBookProviderFactory>();
            services.AddSingleton<IProviderFactory<IContactProvider>, PersonalBookFactory>();
            services.AddSingleton<AddressCollector>();
        });
        var collector = env.Get<AddressCollector>();

        // The own address (anna@example.com, see AddAccountAsync) is never kept.
        var personal = await env.AddAccountAsync(ServiceKind.Contacts, PersonalBookFactory.Id);
        await env.Contacts.SyncConnectionAsync(personal, ct);

        Assert.Equal(2, await collector.CollectAsync([new MailAddress("Rainer Hager", "rainer@example.com"), new MailAddress(null, "info@example.org"), new MailAddress("Anna", "anna@example.com")], ct));
        Assert.Equal(0, await collector.CollectAsync([new MailAddress("Rainer Hager", "RAINER@example.com")], ct));

        var account = Assert.Single(await env.Accounts.GetAccountsAsync(ct), a => a.Connections.Any(c => c.ProviderId == ProviderIds.LocalContacts));
        Assert.Equal("Verwendete Adressen", account.DisplayName);
        var kept = (await env.Contacts.SearchAsync(null, ct)).Where(e => e.AddressBook.RemoteId == AddressCollector.AddressBookId).ToList();
        Assert.Equal(["Rainer Hager", "info@example.org"], kept.Select(e => e.Card.DisplayName).Order(StringComparer.Ordinal));
        Assert.Contains(await env.Contacts.GetRecipientsAsync(ct), r => r.Address == "rainer@example.com");

        // Saved as a real contact: no longer among the used addresses.
        var book = (await env.Contacts.GetAddressBooksAsync(ct)).Single(b => b.ConnectionId == personal.Id);
        await env.Contacts.SaveContactAsync(book, ContactDraft.Empty with { GivenName = "Rainer", FamilyName = "Hager", Emails = [new ContactField("rainer@example.com")] }, cancellationToken: ct);
        kept = (await env.Contacts.SearchAsync(null, ct)).Where(e => e.AddressBook.RemoteId == AddressCollector.AddressBookId).ToList();
        Assert.Equal(["info@example.org"], kept.Select(e => e.Card.DisplayName));
    }

    [Theory]
    [InlineData("Rainer Hager", "Rainer", "Hager")]
    [InlineData("Hager, Rainer", "Rainer", "Hager")]
    [InlineData("\"Maria Anna von Arx\"", "Maria Anna von", "Arx")]
    [InlineData("Swisscom", null, "Swisscom")]
    [InlineData("info@example.org", null, null)]
    [InlineData(null, null, null)]
    public void Names_are_split_into_given_and_family_name(string? name, string? given, string? family) =>
        Assert.Equal((given, family), AddressCollector.SplitName(name));

    [Fact]
    public void Receipts_travel_as_headers_and_the_delivery_request_is_taken_off()
    {
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress("Anna Muster", "anna@example.com"));
        Receipts.Request(message, delivery: true, read: true);

        Assert.True(Receipts.WantsReadReceipt(message));
        Assert.Contains("anna@example.com", message.Headers[HeaderId.DispositionNotificationTo], StringComparison.Ordinal);
        Assert.True(Receipts.TakeDeliveryRequest(message));
        Assert.False(Receipts.TakeDeliveryRequest(message)); // gone before the mail goes out

        var plain = new MimeMessage();
        plain.From.Add(new MailboxAddress("Anna Muster", "anna@example.com"));
        Receipts.Request(plain, delivery: false, read: false);
        Assert.False(Receipts.WantsReadReceipt(plain));
        Assert.False(Receipts.TakeDeliveryRequest(plain));
    }

    /// <summary>A writable address book that is not "Verwendete Adressen".</summary>
    [Fact]
    public async Task Contact_certificates_are_kept_until_contacts_change()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var env = await TestEnvironment.CreateAsync(services => services.AddSingleton<IProviderFactory<IContactProvider>, PersonalBookFactory>());
        var personal = await env.AddAccountAsync(ServiceKind.Contacts, PersonalBookFactory.Id);
        await env.Contacts.SyncConnectionAsync(personal, ct);
        var certificates = env.Contacts;
        Assert.Empty(await certificates.GetAllAsync(ct));

        // A contact saved with a certificate: read again, not the empty list from before.
        var book = (await env.Contacts.GetAddressBooksAsync(ct)).Single(b => b.ConnectionId == personal.Id);
        var der = TestPki.User("Lea Keller", "lea@example.com").Certificate.GetEncoded();
        await env.Contacts.SaveContactAsync(book, ContactDraft.Empty with { GivenName = "Lea", Emails = [new ContactField("lea@example.com")], Certificates = [der] }, cancellationToken: ct);
        var (found, emails) = Assert.Single(await certificates.GetAllAsync(ct));
        Assert.Equal(der, found);
        Assert.Contains("lea@example.com", emails);
        Assert.Same(await certificates.GetAllAsync(ct), await certificates.GetAllAsync(ct));
    }

    private sealed class PersonalBookFactory : IProviderFactory<IContactProvider>
    {
        public const string Id = "test-personal";

        public string ProviderId => Id;

        public string DisplayName => "Persönlich";

        public IContactProvider Create(ServiceConnection connection) => new Provider(connection.Id);

        private sealed class Provider(Guid connectionId) : IContactProvider
        {
            public ContactProviderCapabilities Capabilities => ContactProviderCapabilities.Write;

            public Task TestConnectionAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

            public Task<IReadOnlyList<AddressBookInfo>> GetAddressBooksAsync(CancellationToken cancellationToken = default) =>
                Task.FromResult<IReadOnlyList<AddressBookInfo>>([new AddressBookInfo(connectionId, "/personal/", "Persönlich", false)]);

            public Task<AddressBookSyncResult> SyncAddressBookAsync(AddressBookInfo addressBook, IReadOnlyDictionary<string, string?> knownVersions, CancellationToken cancellationToken = default) =>
                Task.FromResult(new AddressBookSyncResult("s", false, [], []));

            public Task<ContactObject> SaveAsync(AddressBookInfo addressBook, ContactObject contact, CancellationToken cancellationToken = default) =>
                Task.FromResult(contact with { RemoteId = string.IsNullOrEmpty(contact.RemoteId) ? Guid.NewGuid().ToString("N") : contact.RemoteId });

            public Task DeleteAsync(AddressBookInfo addressBook, ContactObject contact, CancellationToken cancellationToken = default) => Task.CompletedTask;

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
