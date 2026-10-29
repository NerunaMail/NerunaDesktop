using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Neruna.Core.Security;

namespace Neruna.Storage;

/// <summary>
/// A Neruna of its own with own accounts and settings, in its own window: e.g. "Business" on one screen and "Privat" on
/// the other. The standard profile is the data folder itself (installations before profiles keep everything as it was);
/// further ones live in <c>profiles/&lt;folder&gt;</c> below it.
/// </summary>
/// <param name="Folder">Relative to the data folder; empty for the standard profile.</param>
public sealed record Profile(Guid Id, string Name, string Folder)
{
    [JsonIgnore]
    public bool IsStandard => Id == Guid.Empty;
}

/// <summary>
/// <c>profiles.json</c> in the data folder: the profiles and which one opens without <c>--profile</c>. Missing: only the
/// standard profile. Read at every start, written by Einstellungen → Profile.
/// </summary>
public sealed class ProfileList
{
    public const string FileName = "profiles.json";

    /// <summary>The command line argument: <c>--profile Business</c> (name or id).</summary>
    public const string Argument = "--profile";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly string _root;
    private readonly List<Profile> _profiles;

    private ProfileList(string root, List<Profile> profiles, Guid defaultId, bool askAtStart)
    {
        _root = root;
        _profiles = profiles;
        DefaultId = profiles.Any(p => p.Id == defaultId) ? defaultId : Guid.Empty;
        AskAtStart = askAtStart;
    }

    /// <summary>The standard profile first, then the others in the order they were made.</summary>
    public IReadOnlyList<Profile> All => _profiles;

    public Guid DefaultId { get; private set; }

    public Profile Default => _profiles.First(p => p.Id == DefaultId);

    /// <summary>"Bei jedem Start Profil abfragen": a start without <c>--profile</c> asks which one (if there are several).</summary>
    public bool AskAtStart { get; private set; }

    /// <summary>
    /// Whether this start asks for the profile: set, several profiles, and nothing given – a link or file the system
    /// opens goes to the default one without a question.
    /// </summary>
    public bool AsksFor(IReadOnlyList<string> args) => AskAtStart && _profiles.Count > 1 && NameIn(args) is null && WithoutProfile(args).Length == 0;

    public void SetAskAtStart(bool ask)
    {
        AskAtStart = ask;
        Save();
    }

    public static ProfileList Load(string root)
    {
        ArgumentNullException.ThrowIfNull(root);
        Stored? stored = null;
        try
        {
            var path = Path.Combine(root, FileName);
            if (File.Exists(path))
            {
                stored = JsonSerializer.Deserialize<Stored>(File.ReadAllText(path), Json);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Unreadable: the standard profile opens; the file stays for the user to look at.
        }

        var profiles = new List<Profile> { new(Guid.Empty, stored?.Profiles?.FirstOrDefault(p => p.Id == Guid.Empty)?.Name ?? "Standard", string.Empty) };
        foreach (var profile in stored?.Profiles ?? [])
        {
            if (profile.Id != Guid.Empty && IsInside(profile.Folder) && profiles.All(p => p.Id != profile.Id))
            {
                profiles.Add(profile);
            }
        }

        return new ProfileList(root, profiles, stored?.Default ?? Guid.Empty, stored?.AskAtStart ?? false);
    }

    /// <summary>The profile <c>--profile</c> names (its name, ignoring case, or its id); none named: the default one; an unknown name: null.</summary>
    public Profile? Resolve(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var named = NameIn(args);
        return named is null ? Default : Find(named);
    }

    public Profile? Find(string nameOrId) =>
        Guid.TryParse(nameOrId, out var id)
            ? _profiles.FirstOrDefault(p => p.Id == id)
            : _profiles.FirstOrDefault(p => string.Equals(p.Name, nameOrId.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>What follows <c>--profile</c> (or <c>--profile=…</c>), if anything.</summary>
    public static string? NameIn(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        for (var i = 0; i < args.Count; i++)
        {
            if (args[i].StartsWith(Argument + "=", StringComparison.OrdinalIgnoreCase))
            {
                return args[i][(Argument.Length + 1)..];
            }

            if (string.Equals(args[i], Argument, StringComparison.OrdinalIgnoreCase) && i + 1 < args.Count)
            {
                return args[i + 1];
            }
        }

        return null;
    }

    /// <summary>The arguments without <c>--profile …</c>: what a later start hands over (links, files) once it is in the right profile.</summary>
    public static string[] WithoutProfile(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var rest = new List<string>();
        for (var i = 0; i < args.Count; i++)
        {
            if (string.Equals(args[i], Argument, StringComparison.OrdinalIgnoreCase))
            {
                i++;
            }
            else if (!args[i].StartsWith(Argument + "=", StringComparison.OrdinalIgnoreCase))
            {
                rest.Add(args[i]);
            }
        }

        return [.. rest];
    }

    public string DirectoryOf(Profile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return profile.IsStandard ? _root : Path.Combine(_root, profile.Folder);
    }

    /// <summary>A new profile with an empty folder of its own (Neruna sets it up at its first start).</summary>
    public Profile Add(string name)
    {
        var trimmed = CheckName(name, null);
        var id = Guid.NewGuid();
        var slug = new string([.. trimmed.ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-')]).Trim('-');
        var folder = Path.Combine("profiles", (slug.Length == 0 ? "profil" : slug[..Math.Min(slug.Length, 24)]) + "-" + id.ToString("N")[..6]);
        var profile = new Profile(id, trimmed, folder);
        Directory.CreateDirectory(DirectoryOf(profile));
        _profiles.Add(profile);
        Save();
        return profile;
    }

    public void Rename(Guid id, string name)
    {
        var index = _profiles.FindIndex(p => p.Id == id);
        if (index < 0)
        {
            return;
        }

        _profiles[index] = _profiles[index] with { Name = CheckName(name, id) };
        Save();
    }

    public void SetDefault(Guid id)
    {
        DefaultId = _profiles.Any(p => p.Id == id) ? id : Guid.Empty;
        Save();
    }

    /// <summary>
    /// Takes the profile off the list and deletes its folder, after <paramref name="deleteSecrets"/> removed its
    /// passwords from the system's credential store. Not the standard profile.
    /// </summary>
    public async Task RemoveAsync(Guid id, Func<Profile, Task> deleteSecrets)
    {
        ArgumentNullException.ThrowIfNull(deleteSecrets);
        if (_profiles.FirstOrDefault(p => p.Id == id) is not { IsStandard: false } profile)
        {
            return;
        }

        await deleteSecrets(profile);
        _profiles.Remove(profile);
        if (DefaultId == id)
        {
            DefaultId = Guid.Empty;
        }

        Save();
        var folder = DirectoryOf(profile);
        if (Directory.Exists(folder))
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    private string CheckName(string name, Guid? self)
    {
        var trimmed = name?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            throw new ArgumentException("A profile needs a name.", nameof(name));
        }

        if (_profiles.Any(p => p.Id != self && string.Equals(p.Name, trimmed, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException("A profile with this name exists already.", nameof(name));
        }

        return trimmed;
    }

    // Written whole and then moved: another Neruna reading it at its start never sees half a file.
    private void Save()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, FileName);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(new Stored(DefaultId == Guid.Empty ? null : DefaultId, _profiles, AskAtStart ? true : null), Json));
        File.Move(temporary, path, overwrite: true);
    }

    // Only folders below the data folder: a changed file cannot point Neruna elsewhere (or at the standard profile).
    private static bool IsInside(string folder) =>
        !string.IsNullOrWhiteSpace(folder) && !Path.IsPathRooted(folder) && !folder.Split('/', '\\').Contains("..");

    private sealed record Stored(Guid? Default, List<Profile>? Profiles, bool? AskAtStart = null);
}

/// <summary>
/// The system's credential store for a profile other than the standard one: every entry under an id of its own, made
/// from the profile's id – fixed entries (Neruna Cloud's device key, the AI sign-in …) would otherwise be shared by all
/// profiles. The standard profile uses the ids as they are, as before profiles. The entries made are listed in
/// <c>credentials.ids</c> in the profile's folder (ids only), so deleting the profile can remove them.
/// </summary>
public sealed class ProfileCredentialStore(ICredentialStore inner, Guid profileId, string directory) : ICredentialStore
{
    public const string IndexFile = "credentials.ids";

    private readonly Lock _gate = new();

    public string Location => inner.Location;

    /// <summary>The system's store itself (deleting another profile's entries).</summary>
    public ICredentialStore Inner => inner;

    public Task<string?> GetSecretAsync(Guid connectionId, CancellationToken cancellationToken = default) =>
        inner.GetSecretAsync(Scoped(profileId, connectionId), cancellationToken);

    public async Task SetSecretAsync(Guid connectionId, string secret, CancellationToken cancellationToken = default)
    {
        var id = Scoped(profileId, connectionId);
        await inner.SetSecretAsync(id, secret, cancellationToken);
        Update(ids => ids.Add(id));
    }

    public async Task DeleteSecretAsync(Guid connectionId, CancellationToken cancellationToken = default)
    {
        var id = Scoped(profileId, connectionId);
        await inner.DeleteSecretAsync(id, cancellationToken);
        Update(ids => ids.Remove(id));
    }

    /// <summary>Removes every entry the profile in <paramref name="directory"/> made (before its folder is deleted).</summary>
    public static async Task DeleteAllAsync(ICredentialStore store, string directory, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        foreach (var id in Read(Path.Combine(directory, IndexFile)))
        {
            await store.DeleteSecretAsync(id, cancellationToken);
        }
    }

    /// <summary>The id an entry of <paramref name="profile"/> is kept under.</summary>
    public static Guid Scoped(Guid profile, Guid id)
    {
        Span<byte> input = stackalloc byte[32];
        profile.TryWriteBytes(input[..16]);
        id.TryWriteBytes(input[16..]);
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(input, hash);
        return new Guid(hash[..16]);
    }

    private void Update(Func<HashSet<Guid>, bool> change)
    {
        var path = Path.Combine(directory, IndexFile);
        lock (_gate)
        {
            var ids = Read(path);
            if (change(ids))
            {
                File.WriteAllText(path, string.Join('\n', ids.Select(i => i.ToString("N"))), Encoding.UTF8);
            }
        }
    }

    private static HashSet<Guid> Read(string path) =>
        File.Exists(path)
            ? [.. File.ReadAllLines(path).Select(l => Guid.TryParse(l, out var id) ? id : Guid.Empty).Where(id => id != Guid.Empty)]
            : [];
}
