using System.Globalization;
using System.Text;
using System.Text.Json;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;

namespace Neruna.Core.Cloud;

/// <summary>What the check of a server's certificate found.</summary>
public enum ServerCertificateStatus
{
    /// <summary>Signed by Neruna, not expired, names the server's host.</summary>
    Valid,

    /// <summary>The server has none (or answered something else).</summary>
    Missing,

    /// <summary>Not signed by Neruna, or unreadable.</summary>
    Invalid,

    /// <summary>Signed, but for other host names.</summary>
    WrongHost,

    Expired,
}

/// <summary>
/// The server certificate of a Neruna server (<c>GET /api/v1/server-certificate</c>, see contract/openapi.yaml): signed
/// by Neruna (Ed25519), it names the host names of the server. The apps connect only to a server whose certificate
/// names the host of the address they were given.
/// </summary>
public static class ServerCertificate
{
    /// <summary>Neruna's public key (Ed25519). Tests set a key of their own.</summary>
    internal static byte[] TrustedKey { get; set; } = Convert.FromBase64String("7Gv4qUYZT0MUiA5GLDpos3RZ4jOSf3qAi1NRbHZQQ/o=");

    private const int SignatureSize = 64;

    /// <summary>Checks the server's answer (JSON <c>{data, signature}</c>; null: none) for <paramref name="server"/>.</summary>
    public static ServerCertificateStatus Check(string? json, Uri server, DateTimeOffset now) => Check(json, server, now, TrustedKey);

    public static ServerCertificateStatus Check(string? json, Uri server, DateTimeOffset now, byte[] publicKey)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(publicKey);
#if DEBUG
        // Development against a server on this computer (debug builds only).
        if (server.IsLoopback)
        {
            return ServerCertificateStatus.Valid;
        }
#endif
        if (string.IsNullOrWhiteSpace(json))
        {
            return ServerCertificateStatus.Missing;
        }

        byte[] data;
        byte[] signature;
        try
        {
            using var answer = JsonDocument.Parse(json);
            if (answer.RootElement.ValueKind != JsonValueKind.Object
                || !answer.RootElement.TryGetProperty("data", out var dataValue) || dataValue.ValueKind != JsonValueKind.String
                || !answer.RootElement.TryGetProperty("signature", out var signatureValue) || signatureValue.ValueKind != JsonValueKind.String)
            {
                return ServerCertificateStatus.Missing;
            }

            data = Convert.FromBase64String(dataValue.GetString()!);
            signature = Convert.FromBase64String(signatureValue.GetString()!);
        }
        catch (Exception ex) when (ex is JsonException or FormatException)
        {
            return ServerCertificateStatus.Missing;
        }

        if (publicKey.Length != Ed25519PublicKeyParameters.KeySize || signature.Length != SignatureSize || !Verify(data, signature, publicKey))
        {
            return ServerCertificateStatus.Invalid;
        }

        try
        {
            using var payload = JsonDocument.Parse(Encoding.UTF8.GetString(data));
            var root = payload.RootElement;
            if (!root.TryGetProperty("expires_at", out var expires) || expires.ValueKind != JsonValueKind.String
                || !DateTimeOffset.TryParse(expires.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var expiresAt)
                || !root.TryGetProperty("hosts", out var hosts) || hosts.ValueKind != JsonValueKind.Array)
            {
                return ServerCertificateStatus.Invalid;
            }

            if (expiresAt <= now)
            {
                return ServerCertificateStatus.Expired;
            }

            var host = server.IdnHost.TrimEnd('.');
            return hosts.EnumerateArray().Any(h => h.ValueKind == JsonValueKind.String && string.Equals(h.GetString()?.TrimEnd('.'), host, StringComparison.OrdinalIgnoreCase))
                ? ServerCertificateStatus.Valid
                : ServerCertificateStatus.WrongHost;
        }
        catch (JsonException)
        {
            return ServerCertificateStatus.Invalid;
        }
    }

    private static bool Verify(byte[] data, byte[] signature, byte[] publicKey)
    {
        var signer = new Ed25519Signer();
        signer.Init(false, new Ed25519PublicKeyParameters(publicKey, 0));
        signer.BlockUpdate(data, 0, data.Length);
        return signer.VerifySignature(signature);
    }
}
