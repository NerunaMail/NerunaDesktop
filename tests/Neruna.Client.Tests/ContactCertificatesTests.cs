using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Neruna.Core.Contacts;
using Neruna.Core.Security;
using Neruna.Providers.Graph;

namespace Neruna.Client.Tests;

public class ContactCertificatesTests
{
    private const string Card3 = "BEGIN:VCARD\r\nVERSION:3.0\r\nUID:1\r\nFN:Lea Keller\r\nEMAIL:lea@example.com\r\nEND:VCARD\r\n";
    private const string Card4 = "BEGIN:VCARD\r\nVERSION:4.0\r\nUID:2\r\nFN:Marco\r\nEMAIL:marco@example.com\r\nEND:VCARD\r\n";

    [Fact]
    public void Certificate_is_written_as_the_vcard_version_expects_and_read_back_once()
    {
        var der = TestPki.User("Lea Keller", "lea@example.com").Certificate.GetEncoded();

        var three = ContactCertificates.Add(Card3, der);
        Assert.Contains("KEY;ENCODING=b;TYPE=X509:", three, StringComparison.Ordinal);
        Assert.Equal(three, ContactCertificates.Add(three, der));
        Assert.Equal(der, Assert.Single(ContactCertificates.Read(three)));
        Assert.Equal("Lea Keller", VCardReader.Read(three).DisplayName);
        Assert.EndsWith("END:VCARD\r\n", three, StringComparison.Ordinal);

        var four = ContactCertificates.Add(Card4, der);
        Assert.Contains("KEY:data:application/pkix-cert;base64,", four, StringComparison.Ordinal);
        Assert.Equal(der, Assert.Single(ContactCertificates.Read(four)));
    }

    [Fact]
    public void Removing_keeps_other_keys_and_pgp_keys_are_no_certificates()
    {
        var first = TestPki.User("Lea", "lea@example.com").Certificate.GetEncoded();
        var second = TestPki.User("Lea neu", "lea@example.com").Certificate.GetEncoded();
        var card = ContactCertificates.Add(ContactCertificates.Add(Card3, first), second)
            .Replace("END:VCARD", "KEY;TYPE=PGP;ENCODING=b:bWVpbiBwZ3Aga2V5\r\nEND:VCARD", StringComparison.Ordinal);
        Assert.Equal(2, ContactCertificates.Read(card).Count);

        var removed = ContactCertificates.Remove(card, first);
        Assert.Equal(second, Assert.Single(ContactCertificates.Read(removed)));
        Assert.Contains("TYPE=PGP", removed, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Collected_certificate_goes_to_the_contact_and_only_without_one_onto_this_device()
    {
        var contacts = new FakeContactCertificates("lea@example.com");
        await using var env = await TestEnvironment.CreateAsync(s => s.AddSingleton<IContactCertificates>(contacts));
        var manager = env.Get<CertificateManager>();
        var ct = TestContext.Current.CancellationToken;

        var lea = TestPki.User("Lea Keller", "lea@example.com").Certificate;
        var nadia = TestPki.User("Nadia Rossi", "nadia@example.com").Certificate;
        Assert.True(await manager.CollectAsync(lea, ct));
        Assert.True(await manager.CollectAsync(nadia, ct));
        Assert.False(await manager.CollectAsync(lea, ct));

        Assert.Single(contacts.Stored);
        var all = await manager.GetAllAsync(ct);
        Assert.Equal(CertificateSource.Contact, all.Single(c => c.Covers("lea@example.com")).Source);
        Assert.Equal(CertificateSource.CollectedFromMail, all.Single(c => c.Covers("nadia@example.com")).Source);

        // Encrypting to Lea uses the contact's certificate.
        Assert.Contains((await manager.LoadMaterialAsync(ct)).Infos, i => i.Covers("lea@example.com"));

        // Nadia gets a contact: her certificate moves there.
        contacts.Addresses.Add("nadia@example.com");
        Assert.Equal(1, await manager.MoveToContactsAsync(ct));
        Assert.All(await manager.GetAllAsync(ct), c => Assert.Equal(CertificateSource.Contact, c.Source));

        await manager.RemoveAsync(Neruna.Core.Security.CertificateParser.Thumbprint(lea), ct);
        Assert.DoesNotContain(await manager.GetAllAsync(ct), c => c.Covers("lea@example.com"));
    }

    [Fact]
    public void Microsoft_365_contacts_keep_certificates_in_the_outlook_property()
    {
        var der = TestPki.User("Lea Keller", "lea@example.com").Certificate.GetEncoded();
        var graph = JsonSerializer.SerializeToElement(GraphContactProvider.ToGraph(ContactCertificates.Add(Card3, der)));
        var property = Assert.Single(graph.GetProperty("multiValueExtendedProperties").EnumerateArray());
        Assert.Equal("BinaryArray 0x3A70", property.GetProperty("id").GetString());
        Assert.Equal(Convert.ToBase64String(der), Assert.Single(property.GetProperty("value").EnumerateArray()).GetString());

        var contact = JsonDocument.Parse($$"""
            {"id":"C1","givenName":"Lea","surname":"Keller","emailAddresses":[{"address":"lea@example.com"}],
             "multiValueExtendedProperties":[{"id":"BinaryArray 0x3a70","value":["{{Convert.ToBase64String(der)}}"]}]}
            """).RootElement;
        Assert.Equal(der, Assert.Single(ContactCertificates.Read(GraphContactProvider.ToVCard(contact))));
        Assert.False(JsonSerializer.SerializeToElement(GraphContactProvider.ToGraph(Card3)).TryGetProperty("multiValueExtendedProperties", out _));
    }

    private sealed class FakeContactCertificates(params string[] addresses) : IContactCertificates
    {
        public HashSet<string> Addresses { get; } = new(addresses, StringComparer.OrdinalIgnoreCase);

        public List<(byte[] Der, IReadOnlyList<string> Emails)> Stored { get; } = [];

        public Task<IReadOnlyList<(byte[] Der, IReadOnlyList<string> Emails)>> GetAllAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<(byte[], IReadOnlyList<string>)>>([.. Stored]);

        public Task<bool> AddAsync(byte[] der, IReadOnlyCollection<string> emails, CancellationToken cancellationToken = default)
        {
            var matching = emails.Where(Addresses.Contains).ToList();
            if (matching.Count == 0)
            {
                return Task.FromResult(false);
            }

            if (!Stored.Any(s => s.Der.AsSpan().SequenceEqual(der)))
            {
                Stored.Add((der, matching));
            }

            return Task.FromResult(true);
        }

        public Task RemoveAsync(byte[] der, CancellationToken cancellationToken = default)
        {
            Stored.RemoveAll(s => s.Der.AsSpan().SequenceEqual(der));
            return Task.CompletedTask;
        }
    }
}
