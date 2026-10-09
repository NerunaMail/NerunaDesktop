namespace Neruna.Storage.Credentials;

/// <summary>
/// For stores with a small size limit per entry (Windows: 1280 characters): a longer secret – e.g. a sign-in token –
/// is split over the entries "key", "key#1" … "key#n"; the first one only says how many parts follow. Short secrets
/// (all passwords) stay a single entry exactly as before, so existing entries read unchanged.
/// </summary>
internal sealed class ChunkingSecretBackend(ISecretBackend inner, int maxChars) : ISecretBackend
{
    private const string Marker = "neruna-parts:";

    public string Name => inner.Name;

    public string? Get(string key)
    {
        var value = inner.Get(key);
        if (value is null || !value.StartsWith(Marker, StringComparison.Ordinal) || !int.TryParse(value.AsSpan(Marker.Length), out var count))
        {
            return value;
        }

        var parts = new string[count];
        for (var i = 0; i < count; i++)
        {
            // A part missing (written halfway): treat the secret as gone rather than return half of it.
            parts[i] = inner.Get(Part(key, i + 1)) ?? string.Empty;
            if (parts[i].Length == 0)
            {
                return null;
            }
        }

        return string.Concat(parts);
    }

    public void Set(string key, string secret)
    {
        ArgumentNullException.ThrowIfNull(secret);
        var previous = PartCount(key);
        if (secret.Length <= maxChars && !secret.StartsWith(Marker, StringComparison.Ordinal))
        {
            inner.Set(key, secret);
            DeleteParts(key, 1, previous);
            return;
        }

        var count = (secret.Length + maxChars - 1) / maxChars;
        for (var i = 0; i < count; i++)
        {
            inner.Set(Part(key, i + 1), secret.Substring(i * maxChars, Math.Min(maxChars, secret.Length - (i * maxChars))));
        }

        inner.Set(key, Marker + count);
        DeleteParts(key, count + 1, previous);
    }

    public void Delete(string key)
    {
        var count = PartCount(key);
        inner.Delete(key);
        DeleteParts(key, 1, count);
    }

    private int PartCount(string key) =>
        inner.Get(key) is { } value && value.StartsWith(Marker, StringComparison.Ordinal) && int.TryParse(value.AsSpan(Marker.Length), out var count) ? count : 0;

    private void DeleteParts(string key, int from, int to)
    {
        for (var i = from; i <= to; i++)
        {
            inner.Delete(Part(key, i));
        }
    }

    private static string Part(string key, int index) => $"{key}#{index}";
}
