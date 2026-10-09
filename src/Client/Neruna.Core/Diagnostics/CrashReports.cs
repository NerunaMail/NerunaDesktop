using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Neruna.Contracts;
using Neruna.Contracts.Cloud;
using Neruna.Core.Accounts;
using Neruna.Core.Cloud;

namespace Neruna.Core.Diagnostics;

/// <summary>Setting "Absturzberichte": ask each time (default), send without asking, or never.</summary>
public enum CrashReportMode
{
    Ask,
    Always,
    Never,
}

/// <summary>
/// One error as it happened on this computer – unchanged, kept locally (like the log) until the user decides. Only
/// <see cref="CrashReportBuilder.ToRequest"/> makes what leaves the computer, cleaned.
/// </summary>
public sealed record CrashRecord(
    DateTimeOffset OccurredAt,
    bool Fatal,
    string AppVersion,
    string ExceptionType,
    string Message,
    string Stack,
    string Fingerprint,
    string? Log);

/// <summary>A stored crash and the cleaned report that would be sent for it.</summary>
public sealed record PendingCrash(string File, CrashRecord Record, CrashReportRequest Report);

public static partial class CrashReportBuilder
{
    private const int StackFrames = 6;

    public static CrashRecord Capture(Exception exception, bool fatal, string appVersion, string? log, DateTimeOffset occurredAt)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var inner = exception.GetBaseException();
        var type = (inner.GetType().FullName ?? inner.GetType().Name);
        return new CrashRecord(occurredAt, fatal, appVersion, type, inner.Message, exception.ToString(), Fingerprint(exception), log);
    }

    /// <summary>
    /// The same error = the same fingerprint, whatever the message says (it often contains names or numbers): the type
    /// of the innermost exception and its top stack frames (method names only).
    /// </summary>
    public static string Fingerprint(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var inner = exception.GetBaseException();
        var source = inner.StackTrace is { Length: > 0 } ? inner : exception;
        var frames = FrameRegex().Matches(source.StackTrace ?? string.Empty).Take(StackFrames).Select(m => m.Groups[1].Value);
        var text = string.Join('\n', [inner.GetType().FullName ?? inner.GetType().Name, .. frames]);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }

    /// <summary>What is sent: system details and the error, with addresses, names, hosts, paths and tokens removed.</summary>
    public static CrashReportRequest ToRequest(CrashRecord record, string language, IEnumerable<string> sensitive)
    {
        ArgumentNullException.ThrowIfNull(record);
        var words = sensitive.Where(s => s.Trim().Length >= 3).Select(s => s.Trim()).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(s => s.Length).ToList();
        return new CrashReportRequest(
            record.Fingerprint,
            Limit(record.AppVersion, 32),
            Limit($"{RuntimeInformation.OSDescription.Trim()} {RuntimeInformation.OSArchitecture}", 120),
            Limit(RuntimeInformation.FrameworkDescription, 60),
            Limit(language, 8),
            record.Fatal,
            Limit(record.ExceptionType, 255),
            Limit(Scrub(record.Message, words), 4000),
            Limit(Scrub(record.Stack, words), 60000),
            record.Log is null ? null : LimitStart(Scrub(record.Log, words), 60000),
            record.OccurredAt);
    }

    /// <summary>Removes what could identify a person or a company; <paramref name="sensitive"/> are the known ones.</summary>
    public static string Scrub(string text, IEnumerable<string> sensitive)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(sensitive);
        var result = JwtRegex().Replace(text, "<token>");
        result = EmailRegex().Replace(result, "<email>");
        result = UrlHostRegex().Replace(result, "${scheme}://<host>");

        // Known names and hosts (whole words only), before the paths so their placeholders stay intact.
        foreach (var word in sensitive)
        {
            result = Regex.Replace(result, $@"(?<![\w]){Regex.Escape(word)}(?![\w])", word.Contains('.', StringComparison.Ordinal) ? "<host>" : "<name>", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
        }

        result = WindowsUserPathRegex().Replace(result, "${1}<user>");
        result = UnixUserPathRegex().Replace(result, "${1}<user>");
        return IpRegex().Replace(result, "<ip>");
    }

    /// <summary>Names, addresses and hosts this computer knows: the accounts, the user and computer, the organisation.</summary>
    public static IEnumerable<string> SensitiveWords(IEnumerable<Account> accounts, CloudConnection? cloud)
    {
        ArgumentNullException.ThrowIfNull(accounts);
        foreach (var account in accounts)
        {
            foreach (var identity in account.Identities)
            {
                yield return identity.Email;
                yield return identity.DisplayName;
                if (identity.Email.Split('@') is [var local, var domain])
                {
                    yield return local;
                    yield return domain;
                }
            }

            if (account.Label is { } label)
            {
                yield return label;
            }

            foreach (var (key, value) in account.Connections.SelectMany(c => c.Settings))
            {
                if (key.Contains("user", StringComparison.OrdinalIgnoreCase) || key.Contains("login", StringComparison.OrdinalIgnoreCase))
                {
                    yield return value;
                }
                else if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Host.Length > 0)
                {
                    yield return uri.Host;
                }
                else if (HostRegex().IsMatch(value) || value.Contains('@', StringComparison.Ordinal))
                {
                    yield return value;
                }
            }
        }

        yield return Environment.UserName;
        yield return Environment.MachineName;
        if (cloud is not null)
        {
            yield return cloud.OrganizationName;
            yield return cloud.MemberName;
        }
    }

    private static string Limit(string text, int length) => text.Length <= length ? text : text[..length];

    // The log's end is what matters.
    private static string LimitStart(string text, int length) => text.Length <= length ? text : text[^length..];

    [GeneratedRegex(@"^\s*at ([^\s(]+)", RegexOptions.Multiline)]
    private static partial Regex FrameRegex();

    [GeneratedRegex(@"eyJ[\w-]{8,}\.[\w-]{8,}\.[\w-]+")]
    private static partial Regex JwtRegex();

    [GeneratedRegex(@"[\w.+'-]+@[\w-]+(\.[\w-]+)+")]
    private static partial Regex EmailRegex();

    [GeneratedRegex(@"(?<scheme>[a-z][a-z0-9+.-]*)://[^/\s:?#""'<>]+", RegexOptions.IgnoreCase)]
    private static partial Regex UrlHostRegex();

    [GeneratedRegex(@"([A-Za-z]:\\(?:Users|Dokumente und Einstellungen|Documents and Settings)\\)[^\\/\s""']+", RegexOptions.IgnoreCase)]
    private static partial Regex WindowsUserPathRegex();

    [GeneratedRegex(@"(/(?:home|Users)/)[^/\s""']+")]
    private static partial Regex UnixUserPathRegex();

    [GeneratedRegex(@"\b(?:\d{1,3}\.){3}\d{1,3}\b")]
    private static partial Regex IpRegex();

    [GeneratedRegex(@"^[a-z0-9-]+(\.[a-z0-9-]+)+$", RegexOptions.IgnoreCase)]
    private static partial Regex HostRegex();
}

/// <summary>
/// Crashes waiting for the user's decision, as files in the data directory ("crashes"): written synchronously while
/// the app goes down, read at the next start. At most 20 are kept; the same error is sent once per version and day.
/// </summary>
public sealed class CrashReportStore(string directory)
{
    private const int MaxPending = 20;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly Lock _gate = new();

    public string Directory => directory;

    /// <summary>Never throws: it runs while the app is going down.</summary>
    public void Save(CrashRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        try
        {
            lock (_gate)
            {
                System.IO.Directory.CreateDirectory(directory);
                var name = $"crash-{record.OccurredAt.UtcDateTime:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..8]}.json";
                File.WriteAllText(Path.Combine(directory, name), JsonSerializer.Serialize(record, Json));
                foreach (var old in Files().SkipLast(MaxPending))
                {
                    File.Delete(old);
                }
            }
        }
#pragma warning disable CA1031 // nothing may stop the crash handler
        catch (Exception)
#pragma warning restore CA1031
        {
            // The log has it anyway.
        }
    }

    public IReadOnlyList<(string File, CrashRecord Record)> Pending()
    {
        lock (_gate)
        {
            var result = new List<(string, CrashRecord)>();
            foreach (var file in Files())
            {
                try
                {
                    if (JsonSerializer.Deserialize<CrashRecord>(File.ReadAllText(file), Json) is { } record)
                    {
                        result.Add((file, record));
                        continue;
                    }
                }
                catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
                {
                }

                TryDelete(file);
            }

            return result;
        }
    }

    public void Remove(string file)
    {
        lock (_gate)
        {
            TryDelete(file);
        }
    }

    /// <summary>Whether this error was already sent today for this version.</summary>
    public bool WasSentToday(CrashRecord record, DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(record);
        lock (_gate)
        {
            return ReadSent().TryGetValue(SentKey(record), out var day) && day == Day(today);
        }
    }

    public void MarkSent(CrashRecord record, DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(record);
        lock (_gate)
        {
            // Only today's entries are needed.
            var sent = ReadSent().Where(e => e.Value == Day(today)).ToDictionary();
            sent[SentKey(record)] = Day(today);
            System.IO.Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "sent.json"), JsonSerializer.Serialize(sent, Json));
        }
    }

    private Dictionary<string, string> ReadSent()
    {
        var path = Path.Combine(directory, "sent.json");
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path), Json) ?? [] : [];
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return [];
        }
    }

    private static string SentKey(CrashRecord record) => $"{record.Fingerprint}|{record.AppVersion}";

    private static string Day(DateOnly day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private IEnumerable<string> Files() =>
        System.IO.Directory.Exists(directory) ? System.IO.Directory.GetFiles(directory, "crash-*.json").Order(StringComparer.Ordinal) : [];

    private static void TryDelete(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch (IOException)
        {
        }
    }
}

/// <summary>
/// Crash reports to Neruna (neruna.cloud → Admin → Absturzberichte): what is waiting, cleaned for sending, and the
/// user's setting. Connected to Neruna Cloud: with the device token (organisation and device are known); otherwise
/// anonymous.
/// </summary>
public sealed class CrashReportService(CrashReportStore store, ISettingsStore settings, IAccountStore accounts, CloudController cloud, ILogger<CrashReportService> logger)
{
    public CrashReportStore Store => store;

    public async Task<CrashReportMode> GetModeAsync(CancellationToken cancellationToken = default) =>
        await settings.GetAsync(SettingKeys.CrashReports, cancellationToken) switch
        {
            "always" => CrashReportMode.Always,
            "never" => CrashReportMode.Never,
            _ => CrashReportMode.Ask,
        };

    public Task SetModeAsync(CrashReportMode mode, CancellationToken cancellationToken = default) =>
        settings.SetAsync(SettingKeys.CrashReports, mode switch
        {
            CrashReportMode.Always => "always",
            CrashReportMode.Never => "never",
            _ => null,
        }, cancellationToken);

    /// <summary>The waiting crashes with the cleaned report for each – exactly what <see cref="SendAsync"/> sends.</summary>
    public async Task<IReadOnlyList<PendingCrash>> GetPendingAsync(CancellationToken cancellationToken = default)
    {
        var records = store.Pending();
        if (records.Count == 0)
        {
            return [];
        }

        var words = CrashReportBuilder.SensitiveWords(await accounts.GetAccountsAsync(cancellationToken), await cloud.GetConnectionAsync(cancellationToken)).ToList();
        var language = Localization.Texts.Culture.TwoLetterISOLanguageName;
        return records.Select(r => new PendingCrash(r.File, r.Record, CrashReportBuilder.ToRequest(r.Record, language, words))).ToList();
    }

    /// <summary>Sends and removes them; what could not be sent (offline) stays for the next time.</summary>
    /// <returns>How many were sent.</returns>
    public async Task<int> SendAsync(IEnumerable<PendingCrash> crashes, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(crashes);
        var sent = 0;
        foreach (var crash in crashes)
        {
            var today = DateOnly.FromDateTime(DateTime.Now);
            if (store.WasSentToday(crash.Record, today))
            {
                store.Remove(crash.File);
                continue;
            }

            try
            {
                await cloud.SendCrashReportAsync(crash.Report, cancellationToken);
                store.MarkSent(crash.Record, today);
                store.Remove(crash.File);
                sent++;
            }
            catch (CloudException ex) when (ex.Code == "invalid")
            {
                logger.LogWarning(ex, "Crash report refused; dropped");
                store.Remove(crash.File);
            }
            catch (Exception ex) when (ex is HttpRequestException or CloudException or TaskCanceledException)
            {
                logger.LogInformation(ex, "Crash report not sent; kept for later");
            }
        }

        return sent;
    }

    public void Discard(IEnumerable<PendingCrash> crashes)
    {
        ArgumentNullException.ThrowIfNull(crashes);
        foreach (var crash in crashes)
        {
            store.Remove(crash.File);
        }
    }
}
