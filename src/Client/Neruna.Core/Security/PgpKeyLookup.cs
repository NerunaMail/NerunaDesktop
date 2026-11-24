using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Org.BouncyCastle.Bcpg.OpenPgp;

namespace Neruna.Core.Security;

/// <summary>
/// Finds a recipient's public PGP key online: first on the recipient's own mail domain (Web Key Directory, WKD), then on
/// keys.openpgp.org (which hands out keys only for addresses their owner confirmed). Asked only when the user wants to
/// encrypt or looks for a key – never when a mail is read (that would tell the sender it was opened).
/// </summary>
public sealed class PgpKeyLookup(HttpClient http, ILogger<PgpKeyLookup> logger)
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    public const string KeyServer = "https://keys.openpgp.org";

    /// <summary>The keys found for <paramref name="email"/> that name this address; empty if there are none.</summary>
    public async Task<IReadOnlyList<PgpPublicKeyRing>> FindAsync(string email, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        var at = email.LastIndexOf('@');
        if (at <= 0 || at == email.Length - 1)
        {
            return [];
        }

        foreach (var url in WkdUrls(email))
        {
            if (await FetchAsync(url, email, cancellationToken) is { Count: > 0 } found)
            {
                logger.LogInformation("PGP key found in the recipient's web key directory");
                return found;
            }
        }

        var fromServer = await FetchAsync(new Uri($"{KeyServer}/vks/v1/by-email/{Uri.EscapeDataString(email)}"), email, cancellationToken);
        if (fromServer.Count > 0)
        {
            logger.LogInformation("PGP key found on keys.openpgp.org");
        }

        return fromServer;
    }

    /// <summary>The two WKD addresses for a mail address: the "advanced" one (openpgpkey.domain), then the "direct" one.</summary>
    public static IReadOnlyList<Uri> WkdUrls(string email)
    {
        ArgumentNullException.ThrowIfNull(email);
        var at = email.LastIndexOf('@');
        var local = email[..at];
        var domain = email[(at + 1)..].ToLowerInvariant();
        // WKD names the file by the SHA-1 of the local part: an address, not a secret – required by the standard.
#pragma warning disable CA5350
        var hash = ZBase32(SHA1.HashData(Encoding.UTF8.GetBytes(local.ToLowerInvariant())));
#pragma warning restore CA5350
        var query = "?l=" + Uri.EscapeDataString(local);
        return
        [
            new Uri($"https://openpgpkey.{domain}/.well-known/openpgpkey/{domain}/hu/{hash}{query}"),
            new Uri($"https://{domain}/.well-known/openpgpkey/hu/{hash}{query}"),
        ];
    }

    private async Task<IReadOnlyList<PgpPublicKeyRing>> FetchAsync(Uri url, string email, CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(Timeout);
            using var response = await http.GetAsync(url, timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                return [];
            }

            var data = await response.Content.ReadAsByteArrayAsync(timeout.Token);
            var (rings, _) = PgpKeyParser.Read(data);
            // A directory may hold keys for other addresses too: only those naming this one count.
            return [.. rings.Where(r => PgpKeyParser.Describe(r, false, PgpKeySource.KeyServer, false, DateTimeOffset.MinValue).Covers(email))];
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or FormatException or IOException)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            // Neither address nor host (the host is the address's domain): only that the lookup failed and why.
            logger.LogDebug("No PGP key from a key directory: {Error}", ex.GetType().Name);
            return [];
        }
    }

    // z-base-32 (RFC 6189 alphabet), as WKD names its files.
    private static string ZBase32(byte[] data)
    {
        const string alphabet = "ybndrfg8ejkmcpqxot1uwisza345h769";
        var result = new StringBuilder();
        int buffer = 0, bits = 0;
        foreach (var b in data)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                result.Append(alphabet[(buffer >> (bits - 5)) & 31]);
                bits -= 5;
            }
        }

        if (bits > 0)
        {
            result.Append(alphabet[(buffer << (5 - bits)) & 31]);
        }

        return result.ToString();
    }
}
