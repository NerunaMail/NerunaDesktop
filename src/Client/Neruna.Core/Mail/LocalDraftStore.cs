using System.Collections.Concurrent;
using MimeKit;

namespace Neruna.Core.Mail;

/// <summary>
/// Safety copies of drafts on this device (<c>drafts/&lt;connection&gt;_&lt;id&gt;.eml</c> in the data directory, next to the
/// mail kept offline): written before a draft goes to the server's "Entwürfe" and removed once it is there, sent or
/// discarded. A copy that stays – no connection, or Neruna ended unexpectedly – is put into "Entwürfe" by the next
/// sync of its account (<see cref="MailController"/>), so nothing typed is lost.
/// </summary>
public sealed class LocalDraftStore(string dataDirectory)
{
    private readonly string _directory = Path.Combine(dataDirectory, "drafts");

    // Copies of forms that are open right now: theirs to save or remove, not the sync's to upload.
    private readonly ConcurrentDictionary<string, byte> _open = new(StringComparer.Ordinal);

    private const string Prefix = "neruna-local-draft:";

    /// <summary>The id under which a copy shows in the list of "Entwürfe" (never an id of the server).</summary>
    public static string RemoteIdOf(string key) => Prefix + key;

    /// <summary>True for an id from <see cref="RemoteIdOf"/>: a draft that is only on this device.</summary>
    public static bool IsLocal(string? remoteId) => TryKey(remoteId, out _);

    public static bool TryKey(string? remoteId, out string key)
    {
        key = string.Empty;
        if (remoteId is null || !remoteId.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        // Only what Begin makes (two hex numbers): the key becomes a file name.
        var candidate = remoteId[Prefix.Length..];
        if (candidate.Length != 65 || candidate[32] != '_' || !candidate.All(c => c == '_' || char.IsAsciiHexDigit(c)))
        {
            return false;
        }

        key = candidate;
        return true;
    }

    /// <summary>A copy from the list is opened in a form again: the form's from now on.</summary>
    public void Adopt(string key) => _open[key] = 0;

    public bool Exists(string key) => File.Exists(PathOf(key));

    /// <summary>When the copy was last written (its date in the list).</summary>
    public DateTimeOffset SavedAt(string key) => new(File.GetLastWriteTimeUtc(PathOf(key)), TimeSpan.Zero);

    /// <summary>A form starts writing: the key of its copy (no file yet).</summary>
    public string Begin(Guid connectionId)
    {
        var key = $"{connectionId:N}_{Guid.NewGuid():N}";
        _open[key] = 0;
        return key;
    }

    /// <summary>The form is gone; a copy still there is the next sync's to put into "Entwürfe".</summary>
    public void End(string key) => _open.TryRemove(key, out _);

    public async Task SaveAsync(string key, MimeMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        Directory.CreateDirectory(_directory);
        var path = PathOf(key);
        var temporary = path + ".tmp";
        await using (var file = File.Create(temporary))
        {
            await message.WriteToAsync(file, cancellationToken);
        }

        File.Move(temporary, path, overwrite: true);
    }

    public void Remove(string key)
    {
        try
        {
            File.Delete(PathOf(key));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Stays for the next sync, which puts it into "Entwürfe": a double draft at worst, never a lost one.
        }
    }

    /// <summary>Copies of this connection that no open form owns, oldest first.</summary>
    public IReadOnlyList<string> Waiting(Guid connectionId)
    {
        if (!Directory.Exists(_directory))
        {
            return [];
        }

        return [.. new DirectoryInfo(_directory).EnumerateFiles($"{connectionId:N}_*.eml").OrderBy(f => f.LastWriteTimeUtc)
            .Select(f => Path.GetFileNameWithoutExtension(f.Name)).Where(key => !_open.ContainsKey(key))];
    }

    public async Task<MimeMessage> LoadAsync(string key, CancellationToken cancellationToken = default) =>
        await MimeMessage.LoadAsync(PathOf(key), cancellationToken);

    /// <summary>Copies of connections that no longer exist (account removed) go with them, like their other mail.</summary>
    public void Prune(IReadOnlyCollection<Guid> connections)
    {
        ArgumentNullException.ThrowIfNull(connections);
        if (!Directory.Exists(_directory))
        {
            return;
        }

        var known = connections.Select(c => c.ToString("N")).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(_directory))
        {
            var name = Path.GetFileName(file);
            var orphan = name.EndsWith(".eml", StringComparison.OrdinalIgnoreCase) && !known.Contains(name.Split('_')[0]);
            // A .tmp left by a write that never finished (older than a moment: not one being written now).
            var stale = name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) && File.GetLastWriteTimeUtc(file) < DateTime.UtcNow.AddMinutes(-10);
            if (orphan || stale)
            {
                try
                {
                    File.Delete(file);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Next time.
                }
            }
        }
    }

    private string PathOf(string key) => Path.Combine(_directory, key + ".eml");
}
