using Neruna.Core.Mail;
using Neruna.Providers.Imap.Sieve;
using static Neruna.Core.Localization.Texts;

namespace Neruna.Providers.Imap;

/// <summary>Server rules over ManageSieve: Neruna's block in the active script (see <see cref="SieveRules"/>).</summary>
public sealed partial class ImapMailProvider : IServerRulesProvider
{
    public async Task<ServerRuleSet> GetRulesAsync(CancellationToken cancellationToken = default)
    {
        await using var sieve = await OpenSieveAsync(forRules: true, cancellationToken);
        if (!sieve.Extensions.Contains("fileinto"))
        {
            throw new ServerRulesUnavailableException(T("Der Mailserver kann keine Mails in Ordner verschieben (Sieve ohne «fileinto»)."));
        }

        var active = (await sieve.ListScriptsAsync(cancellationToken)).FirstOrDefault(s => s.Active).Name;
        var script = active is null ? null : await sieve.GetScriptAsync(active, cancellationToken);
        var others = SieveRules.HasOthers(script) ? [F("Weitere Filter im Sieve-Skript «{0}» (z. B. aus dem Webmail)", active ?? string.Empty)] : Array.Empty<string>();
        return new ServerRuleSet(SieveRules.Read(script), RuleFeaturesOf(sieve), others);
    }

    public async Task SaveRulesAsync(IReadOnlyList<ServerRule> rules, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rules);
        await using var sieve = await OpenSieveAsync(forRules: true, cancellationToken);
        var hasCopy = sieve.Extensions.Contains("copy");
        var missing = SieveRules.Needs(rules, hasCopy).Where(n => !sieve.Extensions.Contains(n)).ToList();
        if (missing.Count > 0)
        {
            throw new ServerRulesUnavailableException(F("Der Mailserver kann das nicht (Sieve ohne «{0}»).", string.Join("», «", missing)));
        }

        // A new rule gets an id of its own (the script keeps them).
        rules = [.. rules.Select(r => r.Id is null ? r with { Id = Guid.NewGuid().ToString("N")[..12] } : r)];
        var trash = rules.Any(r => r.IsEnabled && r.Delete)
            ? (await GetFoldersAsync(cancellationToken)).FirstOrDefault(f => f.Role == FolderRole.Trash)?.RemoteId
              ?? throw new ServerRulesUnavailableException(T("Ohne Ordner für gelöschte Elemente kann eine Regel nicht löschen."))
            : null;
        var active = (await sieve.ListScriptsAsync(cancellationToken)).FirstOrDefault(s => s.Active).Name;
        var script = active is null ? null : await sieve.GetScriptAsync(active, cancellationToken);
        try
        {
            await sieve.PutScriptAsync(active ?? OwnScript, SieveRules.Write(script, rules, trash, hasCopy), cancellationToken);
            if (active is null)
            {
                await sieve.SetActiveAsync(OwnScript, cancellationToken);
            }
        }
        catch (ManageSieveException ex)
        {
            throw new ServerRulesUnavailableException(F("Der Server hat die Regeln abgelehnt: {0}", ex.Message), ex);
        }
    }

    private static ServerRuleFeatures RuleFeaturesOf(ManageSieveClient sieve) =>
        ServerRuleFeatures.Forward | ServerRuleFeatures.Delete | ServerRuleFeatures.Stop
        | (sieve.Extensions.Contains("body") ? ServerRuleFeatures.Body : ServerRuleFeatures.None)
        | (sieve.Extensions.Contains("imap4flags") ? ServerRuleFeatures.Flag : ServerRuleFeatures.None);
}
