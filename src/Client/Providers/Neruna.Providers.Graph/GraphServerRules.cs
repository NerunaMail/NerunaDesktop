using System.Net;
using System.Text.Json;
using Neruna.Core.Mail;
using static Neruna.Core.Localization.Texts;

namespace Neruna.Providers.Graph;

/// <summary>
/// Server rules of a Microsoft 365 / Outlook.com mailbox (mailFolders/inbox/messageRules). Rules with conditions or
/// actions Neruna does not show (made in Outlook) are named and left as they are. Microsoft has no "flag" action.
/// Needs MailboxSettings.ReadWrite, like the out-of-office reply.
/// </summary>
internal sealed partial class GraphMailProvider : IServerRulesProvider
{
    private const string RulesPath = "me/mailFolders/inbox/messageRules";

    private static readonly (RuleField Field, string Name)[] ConditionNames =
        [(RuleField.From, "senderContains"), (RuleField.Recipient, "recipientContains"), (RuleField.Subject, "subjectContains"), (RuleField.Body, "bodyContains")];

    private static readonly HashSet<string> KnownActionNames = new(StringComparer.Ordinal) { "moveToFolder", "markAsRead", "forwardTo", "delete", "stopProcessingRules" };

    public async Task<ServerRuleSet> GetRulesAsync(CancellationToken cancellationToken = default)
    {
        var (rules, others) = await ReadRulesAsync(cancellationToken);
        return new ServerRuleSet([.. rules.Select(r => r.Rule)], ServerRuleFeatures.Body | ServerRuleFeatures.Forward | ServerRuleFeatures.Delete | ServerRuleFeatures.Stop,
            [.. others.Select(o => o.Name)]);
    }

    public async Task SaveRulesAsync(IReadOnlyList<ServerRule> rules, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rules);
        var (existing, others) = await ReadRulesAsync(cancellationToken);
        var wanted = rules.Where(r => r.Id is not null).Select(r => r.Id!).ToHashSet(StringComparer.Ordinal);
        try
        {
            foreach (var gone in existing.Where(e => !wanted.Contains(e.Rule.Id!)))
            {
                await graph.SendJsonAsync(HttpMethod.Delete, $"{RulesPath}/{gone.Rule.Id}", null, cancellationToken);
            }

            // Neruna's rules first, in their order; those made elsewhere after them.
            var sequence = 1;
            foreach (var rule in rules)
            {
                var body = RuleBody(rule, sequence++);
                await (rule.Id is null || existing.All(e => e.Rule.Id != rule.Id)
                    ? graph.SendJsonAsync(HttpMethod.Post, RulesPath, body, cancellationToken)
                    : graph.SendJsonAsync(HttpMethod.Patch, $"{RulesPath}/{rule.Id}", body, cancellationToken));
            }

            foreach (var other in others.OrderBy(o => o.Sequence).Where(o => o.Sequence < sequence))
            {
                await graph.SendJsonAsync(HttpMethod.Patch, $"{RulesPath}/{other.Id}", new Dictionary<string, object> { ["sequence"] = sequence++ }, cancellationToken);
            }
        }
        catch (GraphException ex)
        {
            throw new ServerRulesUnavailableException(F("Microsoft hat die Regeln abgelehnt: {0}", ex.Message), ex);
        }
    }

    private async Task<(List<(ServerRule Rule, int Sequence)> Rules, List<(string Id, string Name, int Sequence)> Others)> ReadRulesAsync(CancellationToken cancellationToken)
    {
        if (!await graph.GrantsAsync(MicrosoftAccount.MailboxSettingsScope, cancellationToken))
        {
            throw new ServerRulesUnavailableException(T("Für Serverregeln braucht Neruna eine zusätzliche Berechtigung. Bitte einmal neu bei Microsoft anmelden: Einstellungen → Konten → Bearbeiten → «Erneut bei Microsoft anmelden»."));
        }

        JsonElement answer;
        try
        {
            answer = await graph.GetAsync(RulesPath, cancellationToken);
        }
        catch (GraphException ex) when (ex.Status is HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
        {
            throw new ServerRulesUnavailableException(F("Microsoft gibt die Regeln nicht heraus: {0}", ex.Message), ex);
        }

        var rules = new List<(ServerRule, int)>();
        var others = new List<(string, string, int)>();
        foreach (var item in answer.TryGetProperty("value", out var value) ? value.EnumerateArray() : default)
        {
            var sequence = item.TryGetProperty("sequence", out var s) && s.TryGetInt32(out var n) ? n : int.MaxValue;
            if (ReadRule(item) is { } rule)
            {
                rules.Add((rule, sequence));
            }
            else
            {
                others.Add((item.Str("id") ?? string.Empty, item.Str("displayName") ?? T("Regel ohne Namen"), sequence));
            }
        }

        rules.Sort((a, b) => a.Item2.CompareTo(b.Item2));
        return (rules, others);
    }

    private static IEnumerable<JsonProperty> SetProperties(JsonElement item, string name) =>
        item.TryGetProperty(name, out var part) && part.ValueKind == JsonValueKind.Object
            ? part.EnumerateObject().Where(p => p.Value.ValueKind is not (JsonValueKind.Null or JsonValueKind.False) && !(p.Value.ValueKind == JsonValueKind.Array && p.Value.GetArrayLength() == 0))
            : [];

    // A rule Neruna can show completely, or null (other conditions, exceptions or actions: left as it is).
    private static ServerRule? ReadRule(JsonElement item)
    {
        var conditions = SetProperties(item, "conditions").ToList();
        var actions = SetProperties(item, "actions").ToList();
        if (SetProperties(item, "exceptions").Any()
            || conditions.Any(c => ConditionNames.All(k => k.Name != c.Name))
            || actions.Any(a => !KnownActionNames.Contains(a.Name))
            || conditions.Any(c => c.Value.GetArrayLength() > 1))
        {
            return null;
        }

        var list = conditions.Select(c => new RuleCondition(ConditionNames.First(k => k.Name == c.Name).Field, c.Value[0].GetString() ?? string.Empty)).OrderBy(c => c.Field).ToList();
        bool Is(string name) => actions.Any(a => a.Name == name && a.Value.ValueKind == JsonValueKind.True);
        var forward = actions.Where(a => a.Name == "forwardTo").SelectMany(a => a.Value.EnumerateArray())
            .Select(r => r.TryGetProperty("emailAddress", out var e) ? e.Str("address") : null).OfType<string>().ToList();
        return new ServerRule(
            item.Str("id"),
            item.Str("displayName") ?? string.Empty,
            item.TryGetProperty("isEnabled", out var enabled) && enabled.ValueKind == JsonValueKind.True,
            list,
            actions.FirstOrDefault(a => a.Name == "moveToFolder").Value is { ValueKind: JsonValueKind.String } folder ? folder.GetString() : null,
            Is("markAsRead"),
            false,
            forward.Count == 0 ? null : string.Join(", ", forward),
            Is("delete"),
            Is("stopProcessingRules"));
    }

    private static Dictionary<string, object> RuleBody(ServerRule rule, int sequence)
    {
        var conditions = new Dictionary<string, object>();
        foreach (var (field, name) in ConditionNames)
        {
            var values = rule.Conditions.Where(c => c.Field == field).Select(c => c.Value.Trim()).ToList();
            if (values.Count > 0)
            {
                conditions[name] = values;
            }
        }

        var actions = new Dictionary<string, object>();
        if (rule.Delete)
        {
            actions["delete"] = true;
        }
        else if (rule.MoveTo is { } folder)
        {
            actions["moveToFolder"] = folder;
        }

        if (rule.MarkRead)
        {
            actions["markAsRead"] = true;
        }

        var forward = (rule.ForwardTo ?? string.Empty).Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (forward.Length > 0)
        {
            actions["forwardTo"] = forward.Select(a => new Dictionary<string, object> { ["emailAddress"] = new Dictionary<string, string> { ["address"] = a } }).ToList();
        }

        if (rule.Stop)
        {
            actions["stopProcessingRules"] = true;
        }

        return new Dictionary<string, object>
        {
            ["displayName"] = rule.Name,
            ["sequence"] = sequence,
            ["isEnabled"] = rule.IsEnabled,
            ["conditions"] = conditions,
            ["actions"] = actions,
        };
    }
}
