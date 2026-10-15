using DnsClient;
using DnsClient.Protocol;

namespace Neruna.Providers.Dav;

/// <summary>An SRV record: where a service runs (RFC 2782).</summary>
public sealed record SrvTarget(string Host, int Port, int Priority, int Weight);

/// <summary>The DNS lookups CalDAV/CardDAV discovery needs (replaceable in tests).</summary>
public interface IDavDns
{
    Task<IReadOnlyList<SrvTarget>> SrvAsync(string name, CancellationToken cancellationToken);

    /// <summary>The TXT records of a name, each as one string (split strings joined).</summary>
    Task<IReadOnlyList<string>> TxtAsync(string name, CancellationToken cancellationToken);
}

/// <summary>DNS through the system's name servers. An error or no answer is "no records".</summary>
public sealed class SystemDavDns : IDavDns
{
    public static SystemDavDns Instance { get; } = new();

    private readonly Lazy<LookupClient> _client = new(() => new LookupClient(new LookupClientOptions
    {
        Timeout = TimeSpan.FromSeconds(3),
        Retries = 1,
        UseCache = true,
        ThrowDnsErrors = false,
    }));

    public async Task<IReadOnlyList<SrvTarget>> SrvAsync(string name, CancellationToken cancellationToken)
    {
        try
        {
            var answer = await _client.Value.QueryAsync(name, QueryType.SRV, cancellationToken: cancellationToken);
            return [.. answer.Answers.OfType<SrvRecord>().Select(r => new SrvTarget(r.Target.Value.TrimEnd('.'), r.Port, r.Priority, r.Weight))];
        }
        catch (Exception ex) when (ex is DnsResponseException or System.Net.Sockets.SocketException or InvalidOperationException)
        {
            return [];
        }
    }

    public async Task<IReadOnlyList<string>> TxtAsync(string name, CancellationToken cancellationToken)
    {
        try
        {
            var answer = await _client.Value.QueryAsync(name, QueryType.TXT, cancellationToken: cancellationToken);
            return [.. answer.Answers.OfType<TxtRecord>().Select(r => string.Concat(r.Text))];
        }
        catch (Exception ex) when (ex is DnsResponseException or System.Net.Sockets.SocketException or InvalidOperationException)
        {
            return [];
        }
    }
}

/// <summary>
/// CalDAV/CardDAV announced in the DNS (RFC 6764): <c>_caldavs._tcp.&lt;domain&gt;</c> SRV says host and port, an
/// optional TXT <c>path=…</c> the start path. Only the TLS variants are used – Neruna never looks for unencrypted ones.
/// </summary>
public static class DavServiceRecords
{
    /// <summary>
    /// The URLs to try for a domain, best first: the TXT path, then /.well-known/…, then the root – all at the SRV
    /// target. Empty without (usable) SRV record.
    /// </summary>
    /// <param name="service">"caldav" or "carddav".</param>
    public static async Task<IReadOnlyList<Uri>> CandidatesAsync(IDavDns dns, string domain, string service, Random? random = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dns);
        var name = $"_{service}s._tcp.{domain.Trim().TrimEnd('.')}";

        // Target "." means "no such service here" (RFC 2782).
        if (Best(await dns.SrvAsync(name, cancellationToken), random) is not { Host.Length: > 1 } target)
        {
            return [];
        }

        var paths = (await dns.TxtAsync(name, cancellationToken))
            .SelectMany(t => t.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Where(t => t.StartsWith("path=", StringComparison.OrdinalIgnoreCase))
            .Select(t => t[5..])
            .Where(p => p.StartsWith('/'))
            .Append($"/.well-known/{service}")
            .Append("/")
            .Distinct(StringComparer.Ordinal);
        return [.. paths.Select(p => new UriBuilder(Uri.UriSchemeHttps, target.Host, target.Port, p).Uri)];
    }

    /// <summary>
    /// The record to use (RFC 2782): the lowest priority; among those, chosen at random by weight (weight 0 first, so
    /// it is chosen when all weights are 0).
    /// </summary>
    public static SrvTarget? Best(IReadOnlyList<SrvTarget> records, Random? random = null)
    {
        ArgumentNullException.ThrowIfNull(records);
        if (records.Count <= 1)
        {
            return records.FirstOrDefault();
        }

        var lowest = records.Min(r => r.Priority);
        var candidates = records.Where(r => r.Priority == lowest).OrderBy(r => r.Weight != 0).ToList();
        var total = candidates.Sum(r => r.Weight);
#pragma warning disable CA5394 // Load balancing as RFC 2782 asks, not security.
        var pick = (random ?? Random.Shared).Next(total + 1);
#pragma warning restore CA5394
        var running = 0;
        foreach (var record in candidates)
        {
            running += record.Weight;
            if (running >= pick)
            {
                return record;
            }
        }

        return candidates[^1];
    }
}
