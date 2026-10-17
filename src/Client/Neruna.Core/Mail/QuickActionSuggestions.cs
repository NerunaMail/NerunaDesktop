using System.Text.Json;
using static Neruna.Core.Localization.Texts;

namespace Neruna.Core.Mail;

/// <summary>A suggested quick action: the sender whose mails were moved by hand, and where to.</summary>
public sealed record QuickActionSuggestion(Guid AccountId, string Sender, string SenderName, string FolderRemoteId, string FolderName);

/// <summary>
/// "Auto-Schnellaktion": after the third mail of a sender moved by hand into the same folder of an account, Neruna
/// offers to make a quick action of it (mark as read, move there). Moves by quick actions do not count; trash, junk
/// and drafts never. Senders can be left alone for good ("nicht mehr fragen"); the whole thing can be turned off
/// (Einstellungen → Schnellaktionen).
/// </summary>
public sealed class QuickActionSuggestions(ISettingsStore settings, QuickActionService quickActions)
{
    public const int Threshold = 3;
    private const string CountsKey = "quickactions.suggest.counts";
    private const string NeverKey = "quickactions.suggest.never";
    private const string OffKey = "quickactions.suggest.off";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<bool> IsEnabledAsync(CancellationToken cancellationToken = default) =>
        !await settings.GetBoolAsync(OffKey, cancellationToken: cancellationToken);

    public Task SetEnabledAsync(bool enabled, CancellationToken cancellationToken = default) =>
        settings.SetBoolAsync(OffKey, !enabled, cancellationToken);

    /// <summary>The senders never to ask about again.</summary>
    public async Task<IReadOnlyList<string>> GetNeverAsync(CancellationToken cancellationToken = default) =>
        await settings.GetListAsync(NeverKey, cancellationToken) ?? [];

    public async Task SetNeverAsync(IEnumerable<string> senders, CancellationToken cancellationToken = default) =>
        await settings.SetListAsync(NeverKey, [.. senders.Select(s => s.Trim().ToLowerInvariant()).Where(s => s.Length > 0).Distinct()], cancellationToken);

    /// <summary>A mail moved by hand: counted, and a suggestion once a sender reached <see cref="Threshold"/> for one folder.</summary>
    public async Task<QuickActionSuggestion?> RecordMoveAsync(Guid accountId, string? sender, string? senderName, MailFolder target, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        var address = sender?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(address) || !address.Contains('@', StringComparison.Ordinal)
            || target.Role is FolderRole.Trash or FolderRole.Junk or FolderRole.Drafts or FolderRole.Sent
            || !await IsEnabledAsync(cancellationToken) || (await GetNeverAsync(cancellationToken)).Contains(address))
        {
            return null;
        }

        var key = $"{accountId:N}|{address}|{target.RemoteId}";
        var counts = await CountsAsync(cancellationToken);
        counts[key] = counts.GetValueOrDefault(key) + 1;
        if (counts.Count > 500)
        {
            foreach (var old in counts.Keys.Take(counts.Count - 500).ToList())
            {
                counts.Remove(old);
            }
        }

        var reached = counts[key] >= Threshold;
        if (reached)
        {
            counts.Remove(key); // asked now; "Nein" means: again after three more
        }

        await settings.SetAsync(CountsKey, JsonSerializer.Serialize(counts, Json), cancellationToken);
        if (!reached || await CoveredAsync(accountId, address, target.RemoteId, cancellationToken))
        {
            return null;
        }

        return new QuickActionSuggestion(accountId, address, string.IsNullOrWhiteSpace(senderName) ? address : senderName.Trim(), target.RemoteId, MailFolderTree.DisplayName(target));
    }

    /// <summary>"Ja": the quick action – for this sender in this account: mark as read, move to the folder.</summary>
    public async Task<QuickAction> AcceptAsync(QuickActionSuggestion suggestion, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(suggestion);
        var actions = (await quickActions.GetActionsAsync(cancellationToken)).ToList();
        var color = QuickActionService.Colors[actions.Count % QuickActionService.Colors.Count];
        var label = F("{0} → {1}", Shorten(suggestion.SenderName), suggestion.FolderName);
        var action = new QuickAction(Guid.NewGuid(), label, color,
            new QuickActionCondition { SenderContains = suggestion.Sender, AccountId = suggestion.AccountId },
            [new QuickActionStep(QuickActionStepKind.MarkRead), new QuickActionStep(QuickActionStepKind.MoveToFolder) { FolderRemoteId = suggestion.FolderRemoteId, FolderName = suggestion.FolderName }]);
        actions.Add(action);
        await quickActions.SaveActionsAsync(actions, cancellationToken);
        return action;
    }

    /// <summary>"Nein" – with "bei diesem Absender nicht mehr fragen" for good.</summary>
    public async Task DeclineAsync(QuickActionSuggestion suggestion, bool never, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(suggestion);
        if (never)
        {
            await SetNeverAsync([.. await GetNeverAsync(cancellationToken), suggestion.Sender], cancellationToken);
        }
    }

    private async Task<Dictionary<string, int>> CountsAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await settings.GetAsync(CountsKey, cancellationToken) is { } json
                ? JsonSerializer.Deserialize<Dictionary<string, int>>(json, Json) ?? []
                : [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    // A quick action that already moves this sender's mails there: nothing to suggest.
    private async Task<bool> CoveredAsync(Guid accountId, string address, string folder, CancellationToken cancellationToken) =>
        (await quickActions.GetActionsAsync(cancellationToken)).Any(a =>
            a.When.SenderContains is { } contains && address.Contains(contains.Trim(), StringComparison.OrdinalIgnoreCase)
            && (a.When.AccountId is null || a.When.AccountId == accountId)
            && a.Steps.Any(s => s.Kind == QuickActionStepKind.MoveToFolder && s.FolderRemoteId == folder));

    private static string Shorten(string name) => name.Length > 24 ? name[..23] + "…" : name;
}
