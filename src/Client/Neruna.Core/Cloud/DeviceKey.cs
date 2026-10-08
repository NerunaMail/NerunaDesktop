using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Neruna.Core.Cloud;

/// <summary>
/// The key pair that identifies this computer to the Neruna server (ECDSA P-256). Created when connecting; the private
/// key stays on the device (OS keychain), the server only knows the public key. Sign-in is a short JWT assertion
/// signed with it (ES256, like OAuth private_key_jwt, RFC 7523).
/// </summary>
public sealed class DeviceKey : IDisposable
{
    private readonly ECDsa _key;

    private DeviceKey(ECDsa key) => _key = key;

    public static DeviceKey Create() => new(ECDsa.Create(ECCurve.NamedCurves.nistP256));

    /// <summary>From <see cref="ExportPrivateKey"/> (PKCS#8, Base64).</summary>
    public static DeviceKey Import(string privateKey)
    {
        var key = ECDsa.Create();
        key.ImportPkcs8PrivateKey(Convert.FromBase64String(privateKey), out _);
        return new DeviceKey(key);
    }

    public string ExportPrivateKey() => Convert.ToBase64String(_key.ExportPkcs8PrivateKey());

    /// <summary>SubjectPublicKeyInfo as PEM, as the server expects it.</summary>
    public string PublicKeyPem => _key.ExportSubjectPublicKeyInfoPem();

    /// <summary>A signed assertion for <c>POST /api/v1/token</c>: valid for one minute and only once (random jti).</summary>
    public string CreateAssertion(string deviceId, Uri audience, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(audience);
        var header = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, string> { ["alg"] = "ES256", ["typ"] = "JWT", ["kid"] = deviceId }));
        var claims = new Dictionary<string, object>
        {
            ["iss"] = deviceId,
            ["sub"] = deviceId,
            ["aud"] = audience.AbsoluteUri,
            ["iat"] = now.ToUnixTimeSeconds(),
            ["exp"] = now.AddMinutes(1).ToUnixTimeSeconds(),
            ["jti"] = Guid.NewGuid().ToString("N"),
        };
        var payload = Base64Url(JsonSerializer.SerializeToUtf8Bytes(claims));

        // JWT wants r‖s (IEEE P1363), which is .NET's default signature format for ECDsa.
        var signature = _key.SignData(Encoding.ASCII.GetBytes($"{header}.{payload}"), HashAlgorithmName.SHA256);
        return $"{header}.{payload}.{Base64Url(signature)}";
    }

    public void Dispose() => _key.Dispose();

    private static string Base64Url(byte[] data) => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
