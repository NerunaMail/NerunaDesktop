using Microsoft.Extensions.Logging;
using Neruna.Core.Accounts;
using Neruna.Core.Mail;
using Neruna.Core.Providers;
using static Neruna.Core.Localization.Texts;

namespace Neruna.Core.Contacts;

/// <summary>
/// "Verwendete Adressen": recipients the user wrote to who are in no address book are kept in a local address book,
/// so completion suggests them next time. The address book (an account of its own, on this device only) is made with
/// the first address.
/// </summary>
public sealed class AddressCollector(IAccountStore accounts, ContactController contacts, ILogger<AddressCollector> logger) : IDisposable
{
    public const string AddressBookId = "collected";

    // One at a time: two mails sent at once must not make the address book twice.
    private readonly SemaphoreSlim _gate = new(1, 1);

    public void Dispose() => _gate.Dispose();

    /// <summary>Adds the addresses no address book has yet (own addresses never).</summary>
    /// <returns>How many were added.</returns>
    public async Task<int> CollectAsync(IEnumerable<MailAddress> recipients, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(recipients);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var all = await accounts.GetAccountsAsync(cancellationToken);
            var known = new HashSet<string>(
                (await contacts.SearchAsync(null, cancellationToken)).SelectMany(e => e.Card.EmailAddresses),
                StringComparer.OrdinalIgnoreCase);
            var fresh = recipients
                .Where(r => !string.IsNullOrWhiteSpace(r.Address) && !known.Contains(r.Address) && !all.Any(a => a.Owns(r.Address)))
                .DistinctBy(r => r.Address, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (fresh.Count == 0)
            {
                return 0;
            }

            var book = await AddressBookAsync(all, cancellationToken);
            foreach (var address in fresh)
            {
                var (given, family) = SplitName(address.Name);
                await contacts.SaveContactAsync(book, ContactDraft.Empty with
                {
                    GivenName = given,
                    FamilyName = family,
                    Emails = [new ContactField(address.Address)],
                }, cancellationToken: cancellationToken);
            }

            logger.LogInformation("Kept {Count} new addresses in \"Verwendete Adressen\"", fresh.Count);
            return fresh.Count;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>The local address book, made (as an account of its own) when there is none.</summary>
    private async Task<AddressBookInfo> AddressBookAsync(IReadOnlyList<Account> all, CancellationToken cancellationToken)
    {
        var connection = all.SelectMany(a => a.Connections).FirstOrDefault(c => c.ProviderId == ProviderIds.LocalContacts);
        if (connection is null)
        {
            connection = new ServiceConnection(Guid.NewGuid(), ServiceKind.Contacts, ProviderIds.LocalContacts, new Dictionary<string, string>());
            await accounts.SaveAccountAsync(new Account(Guid.NewGuid(), T("Verwendete Adressen"), null, [connection]), cancellationToken);
        }

        var book = (await contacts.GetAddressBooksAsync(cancellationToken)).FirstOrDefault(b => b.ConnectionId == connection.Id);
        if (book is null)
        {
            await contacts.SyncConnectionAsync(connection, cancellationToken);
            book = (await contacts.GetAddressBooksAsync(cancellationToken)).First(b => b.ConnectionId == connection.Id);
        }

        return book;
    }

    /// <summary>"Rainer Hager" → Rainer / Hager, "Hager, Rainer" → Rainer / Hager, one word → family name.</summary>
    public static (string? Given, string? Family) SplitName(string? name)
    {
        var trimmed = name?.Trim().Trim('"', '\'').Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed.Contains('@', StringComparison.Ordinal))
        {
            return (null, null);
        }

        var comma = trimmed.IndexOf(',', StringComparison.Ordinal);
        if (comma > 0)
        {
            var given = trimmed[(comma + 1)..].Trim();
            return (given.Length == 0 ? null : given, trimmed[..comma].Trim());
        }

        var space = trimmed.LastIndexOf(' ');
        return space < 0 ? (null, trimmed) : (trimmed[..space].Trim(), trimmed[(space + 1)..]);
    }
}
