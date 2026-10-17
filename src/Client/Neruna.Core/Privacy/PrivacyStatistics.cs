using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Neruna.Core.Privacy;

/// <summary>What Neruna's protection did since <see cref="Since"/> (Einstellungen → Datenschutz → Statistik).</summary>
public sealed record PrivacyCounts
{
    public DateTimeOffset Since { get; init; } = DateTimeOffset.Now;

    /// <summary>Mails opened and checked.</summary>
    public int Checked { get; init; }

    /// <summary>Of them: "Vorsicht".</summary>
    public int Suspicious { get; init; }

    /// <summary>Of them: "Gefährlich".</summary>
    public int Dangerous { get; init; }

    /// <summary>Mails with a link to (or from) a known phishing site.</summary>
    public int KnownPhishing { get; init; }

    /// <summary>Mails whose sender failed the mail server's check (SPF, DKIM, DMARC).</summary>
    public int ForgedSenders { get; init; }

    /// <summary>Mails pretending to come from a company, a contact or the own address.</summary>
    public int Impersonations { get; init; }

    /// <summary>Tracking pixels left out.</summary>
    public int TrackersBlocked { get; init; }

    /// <summary>Mails whose pictures from the internet were held back.</summary>
    public int ImagesBlocked { get; init; }

    /// <summary>Clicked links Neruna showed first because they looked suspicious.</summary>
    public int LinksWarned { get; init; }

    /// <summary>Clicked links opened without tracking parameters or redirect.</summary>
    public int LinksCleaned { get; init; }
}

/// <summary>
/// Counts what the protection did, on this device only (nothing is sent). A mail counts once, however often it is
/// opened: the last 5000 opened are remembered by a short hash of their Message-Id.
/// </summary>
public sealed class PrivacyStatistics(ISettingsStore settings) : IDisposable
{
    private const string CountsKey = "privacy.stats";
    private const string SeenKey = "privacy.stats.seen";
    private const int SeenLimit = 5000;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>The app's statistics (set at start), for places without dependency injection (the reading pane).</summary>
    public static PrivacyStatistics? Current { get; set; }

    public event EventHandler? Changed;

    public void Dispose() => _gate.Dispose();

    public async Task<PrivacyCounts> GetAsync(CancellationToken cancellationToken = default)
    {
        var json = await settings.GetAsync(CountsKey, cancellationToken);
        try
        {
            return json is null ? new PrivacyCounts() : JsonSerializer.Deserialize<PrivacyCounts>(json, Json) ?? new PrivacyCounts();
        }
        catch (JsonException)
        {
            return new PrivacyCounts();
        }
    }

    /// <summary>A mail was opened and checked (counted once per mail).</summary>
    public Task RecordMailAsync(string identity, ThreatAssessment assessment, int trackers, bool imagesBlocked, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(assessment);
        return UpdateAsync(identity, c => c with
        {
            Checked = c.Checked + 1,
            Suspicious = c.Suspicious + (assessment.Level == ThreatLevel.Caution ? 1 : 0),
            Dangerous = c.Dangerous + (assessment.Level == ThreatLevel.Dangerous ? 1 : 0),
            KnownPhishing = c.KnownPhishing + (assessment.Findings.Any(f => f.Code == FindingCodes.KnownPhishing) ? 1 : 0),
            ForgedSenders = c.ForgedSenders + (assessment.SenderForged ? 1 : 0),
            Impersonations = c.Impersonations + (assessment.Findings.Any(f => f.Code == FindingCodes.Impersonation) ? 1 : 0),
            TrackersBlocked = c.TrackersBlocked + trackers,
            ImagesBlocked = c.ImagesBlocked + (imagesBlocked ? 1 : 0),
        }, cancellationToken);
    }

    /// <summary>A link was clicked: shown first because suspicious, and/or opened cleaned.</summary>
    public Task RecordLinkAsync(bool warned, bool cleaned, CancellationToken cancellationToken = default) =>
        !warned && !cleaned
            ? Task.CompletedTask
            : UpdateAsync(null, c => c with { LinksWarned = c.LinksWarned + (warned ? 1 : 0), LinksCleaned = c.LinksCleaned + (cleaned ? 1 : 0) }, cancellationToken);

    /// <summary>"Zurücksetzen": from now on.</summary>
    public async Task ResetAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await settings.SetAsync(CountsKey, JsonSerializer.Serialize(new PrivacyCounts(), Json), cancellationToken);
            await settings.SetAsync(SeenKey, null, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private async Task UpdateAsync(string? identity, Func<PrivacyCounts, PrivacyCounts> change, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (identity is not null)
            {
                var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..12];
                var seen = (await settings.GetListAsync(SeenKey, cancellationToken) ?? []).ToList();
                if (seen.Contains(hash, StringComparer.Ordinal))
                {
                    return;
                }

                seen.Add(hash);
                await settings.SetListAsync(SeenKey, seen.Count > SeenLimit ? seen[^SeenLimit..] : seen, cancellationToken);
            }

            await settings.SetAsync(CountsKey, JsonSerializer.Serialize(change(await GetAsync(cancellationToken)), Json), cancellationToken);
        }
        finally
        {
            _gate.Release();
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }
}
