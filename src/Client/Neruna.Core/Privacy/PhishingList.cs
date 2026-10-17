using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Neruna.Core.Privacy;

/// <summary>
/// Known phishing hosts (Phishing.Database, via neruna.cloud/api/v1/phishing/hosts), checked on this device: links and
/// sender domains are looked up here, nothing of a mail leaves the device. 6 bytes of SHA-256 per host, sorted – about
/// 4.5 MB for 750 000 hosts. <see cref="Shared"/> is the list the app loaded; empty until then (nothing is found).
/// </summary>
public sealed class PhishingList
{
    public const int KeyLength = 6;

    private const int HeaderLength = 29;

    private const byte Full = 1;

    private const byte Changes = 2;

    private static readonly IdnMapping Idn = new();

    private static readonly System.Buffers.SearchValues<char> NoHost = System.Buffers.SearchValues.Create(" /@:?#\t");

    private volatile State _state = new([], 0);

    private sealed record State(byte[] Keys, long Version);

    /// <summary>The app's list (desktop and Android load it at start and keep it current).</summary>
    public static PhishingList Shared { get; } = new();

    /// <summary>The version of the list (seconds since 1970 of its build on the server); 0 without a list.</summary>
    public long Version => _state.Version;

    public int Count => _state.Keys.Length / KeyLength;

    /// <summary>The build time of the list; null without one.</summary>
    public DateTimeOffset? BuiltAt => Version > 0 ? DateTimeOffset.FromUnixTimeSeconds(Version) : null;

    /// <summary>Lower case, ASCII (punycode), without trailing dot and leading "www."; null for what is no host name.</summary>
    public static string? Normalize(string? host)
    {
        var value = host?.Trim().TrimEnd('.').ToLowerInvariant();
        if (string.IsNullOrEmpty(value) || value.AsSpan().IndexOfAny(NoHost) >= 0)
        {
            return null;
        }

        if (!Ascii.IsValid(value))
        {
            try
            {
                value = Idn.GetAscii(value).ToLowerInvariant();
            }
            catch (ArgumentException)
            {
                return null;
            }
        }

        if (value.StartsWith("www.", StringComparison.Ordinal))
        {
            value = value[4..];
        }

        return value.Contains('.', StringComparison.Ordinal) ? value : null;
    }

    public static byte[] KeyOf(string normalizedHost)
    {
        ArgumentNullException.ThrowIfNull(normalizedHost);
        return SHA256.HashData(Encoding.ASCII.GetBytes(normalizedHost))[..KeyLength];
    }

    /// <summary>Whether the host or one of its parent domains (down to the registrable domain) is a known phishing host.</summary>
    public bool Contains(string? host)
    {
        var state = _state;
        if (state.Keys.Length == 0 || Normalize(host) is not { } name)
        {
            return false;
        }

        var registrable = DomainNames.Registrable(name) ?? name;
        for (var candidate = name; ;)
        {
            if (Has(state.Keys, KeyOf(candidate)))
            {
                return true;
            }

            var dot = candidate.IndexOf('.', StringComparison.Ordinal);
            if (candidate.Length <= registrable.Length || dot < 0)
            {
                return false;
            }

            candidate = candidate[(dot + 1)..];
        }
    }

    private static bool Has(byte[] keys, ReadOnlySpan<byte> key)
    {
        int low = 0, high = (keys.Length / KeyLength) - 1;
        while (low <= high)
        {
            var middle = (low + high) >>> 1;
            var compared = keys.AsSpan(middle * KeyLength, KeyLength).SequenceCompareTo(key);
            if (compared == 0)
            {
                return true;
            }

            if (compared < 0)
            {
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        return false;
    }

    /// <summary>
    /// Takes a download: a full list replaces this one, changes are applied if they fit this version.
    /// </summary>
    /// <returns>False for changes to another version (then the full list is needed).</returns>
    /// <exception cref="FormatException">Not a list.</exception>
    public bool Apply(ReadOnlySpan<byte> data)
    {
        if (data.Length < HeaderLength || !data[..4].SequenceEqual("NPH1"u8))
        {
            throw new FormatException("No phishing host list.");
        }

        var kind = data[4];
        var version = BinaryPrimitives.ReadInt64BigEndian(data[5..]);
        var baseVersion = BinaryPrimitives.ReadInt64BigEndian(data[13..]);
        var added = (int)BinaryPrimitives.ReadUInt32BigEndian(data[21..]);
        var removed = (int)BinaryPrimitives.ReadUInt32BigEndian(data[25..]);
        if ((long)HeaderLength + (((long)added + removed) * KeyLength) != data.Length)
        {
            throw new FormatException("Phishing host list is cut off.");
        }

        var addedKeys = data.Slice(HeaderLength, added * KeyLength);
        var removedKeys = data.Slice(HeaderLength + (added * KeyLength), removed * KeyLength);
        var state = _state;
        if (kind == Full)
        {
            _state = new State(addedKeys.ToArray(), version);
            return true;
        }

        if (kind != Changes || baseVersion != state.Version)
        {
            return false;
        }

        _state = new State(Merge(state.Keys, addedKeys, removedKeys), version);
        return true;
    }

    // The sorted list with the added keys in and the removed ones out, still sorted.
    private static byte[] Merge(byte[] keys, ReadOnlySpan<byte> added, ReadOnlySpan<byte> removed)
    {
        var result = new List<byte[]>((keys.Length + added.Length) / KeyLength);
        var removedSet = new HashSet<long>();
        for (var i = 0; i < removed.Length; i += KeyLength)
        {
            removedSet.Add(AsLong(removed.Slice(i, KeyLength)));
        }

        for (var i = 0; i < keys.Length; i += KeyLength)
        {
            if (!removedSet.Contains(AsLong(keys.AsSpan(i, KeyLength))))
            {
                result.Add(keys[i..(i + KeyLength)]);
            }
        }

        for (var i = 0; i < added.Length; i += KeyLength)
        {
            result.Add(added.Slice(i, KeyLength).ToArray());
        }

        result.Sort((a, b) => a.AsSpan().SequenceCompareTo(b));
        var merged = new byte[result.Count * KeyLength];
        var at = 0;
        byte[]? last = null;
        foreach (var key in result)
        {
            if (last is not null && key.AsSpan().SequenceEqual(last))
            {
                continue;
            }

            key.CopyTo(merged, at);
            at += KeyLength;
            last = key;
        }

        return merged[..at];
    }

    private static long AsLong(ReadOnlySpan<byte> key)
    {
        Span<byte> padded = stackalloc byte[8];
        key.CopyTo(padded);
        return BinaryPrimitives.ReadInt64BigEndian(padded);
    }

    /// <summary>The list as a full download (what is stored on the device).</summary>
    public byte[] ToFull()
    {
        var state = _state;
        var data = new byte[HeaderLength + state.Keys.Length];
        "NPH1"u8.CopyTo(data);
        data[4] = Full;
        BinaryPrimitives.WriteInt64BigEndian(data.AsSpan(5), state.Version);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(21), (uint)(state.Keys.Length / KeyLength));
        state.Keys.CopyTo(data, HeaderLength);
        return data;
    }

    /// <summary>The list is turned off (Einstellungen → Datenschutz): nothing is found any more.</summary>
    public void Clear() => _state = new State([], 0);
}

/// <summary>
/// Keeps <see cref="PhishingList"/> current: loads the stored copy at start, asks neruna.cloud now and then for what
/// changed (a few KB a day, the full list only the first time) and stores the result. Nothing but the version is sent.
/// </summary>
public sealed class PhishingListUpdater(HttpClient http, string directory, PhishingList? list = null, Uri? server = null)
{
    private readonly PhishingList _list = list ?? PhishingList.Shared;
    private readonly Uri _server = server ?? Cloud.CloudController.DefaultServer;

    private string FilePath => Path.Combine(directory, "phishing-hosts.bin");

    private string CheckedPath => Path.Combine(directory, "phishing-hosts.checked");

    /// <summary>The copy from the last download (at start, before any network).</summary>
    public void LoadStored()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                _list.Apply(File.ReadAllBytes(FilePath));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException)
        {
            // A broken copy: the next download replaces it.
        }
    }

    /// <summary>When the list was last asked for; null if never.</summary>
    public DateTimeOffset? LastChecked => File.Exists(CheckedPath) ? File.GetLastWriteTimeUtc(CheckedPath) : null;

    /// <summary>Asks the server if the last time is longer ago than <paramref name="interval"/>.</summary>
    public async Task<bool> UpdateIfDueAsync(TimeSpan interval, CancellationToken cancellationToken = default)
    {
        if (LastChecked is { } last && DateTimeOffset.UtcNow - last < interval && _list.Version > 0)
        {
            return false;
        }

        await UpdateAsync(cancellationToken);
        return true;
    }

    /// <exception cref="HttpRequestException">The server could not be reached.</exception>
    public async Task UpdateAsync(CancellationToken cancellationToken = default)
    {
        var changed = await DownloadAsync(_list.Version, cancellationToken);
        if (changed is { } data && !_list.Apply(data))
        {
            // Changes to another version than ours (e.g. the stored copy was lost): the full list.
            if (await DownloadAsync(0, cancellationToken) is { } full)
            {
                _list.Apply(full);
            }
        }

        Directory.CreateDirectory(directory);
        if (changed is not null)
        {
            var temporary = FilePath + ".new";
            await File.WriteAllBytesAsync(temporary, _list.ToFull(), cancellationToken);
            File.Move(temporary, FilePath, overwrite: true);
        }

        await File.WriteAllTextAsync(CheckedPath, _list.Version.ToString(CultureInfo.InvariantCulture), cancellationToken);
        File.SetLastWriteTimeUtc(CheckedPath, DateTime.UtcNow);
    }

    // The full list (since = 0) or the changes; null when nothing changed (304).
    private async Task<byte[]?> DownloadAsync(long since, CancellationToken cancellationToken)
    {
        var url = new Uri(_server, since > 0 ? $"api/v1/phishing/hosts?since={since.ToString(CultureInfo.InvariantCulture)}" : "api/v1/phishing/hosts");
        using var response = await http.GetAsync(url, cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.NotModified)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsByteArrayAsync(cancellationToken);
    }
}
