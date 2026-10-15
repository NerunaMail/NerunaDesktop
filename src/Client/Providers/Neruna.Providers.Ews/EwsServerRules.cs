using System.Globalization;
using System.Xml.Linq;
using Neruna.Core.Mail;
using static Neruna.Core.Localization.Texts;

namespace Neruna.Providers.Ews;

/// <summary>
/// Server rules through EWS (GetInboxRules/UpdateInboxRules): Exchange's inbox rules. Rules with conditions or actions
/// Neruna does not show (made in Outlook) are named and left as they are. Exchange has no "flag" action.
/// </summary>
internal sealed partial class EwsMailProvider : IServerRulesProvider
{
    private static readonly (RuleField Field, string Element)[] ConditionElements =
    [
        // In the order of the schema (RulePredicatesType).
        (RuleField.Body, "ContainsBodyStrings"),
        (RuleField.Recipient, "ContainsRecipientStrings"),
        (RuleField.From, "ContainsSenderStrings"),
        (RuleField.Subject, "ContainsSubjectStrings"),
    ];

    private static readonly HashSet<string> KnownActions = new(StringComparer.Ordinal) { "Delete", "ForwardToRecipients", "MarkAsRead", "MoveToFolder", "StopProcessingRules" };

    public async Task<ServerRuleSet> GetRulesAsync(CancellationToken cancellationToken = default)
    {
        var (rules, others) = await ReadRulesAsync(cancellationToken);
        return new ServerRuleSet([.. rules.Select(r => r.Rule)], ServerRuleFeatures.Body | ServerRuleFeatures.Forward | ServerRuleFeatures.Delete | ServerRuleFeatures.Stop, [.. others.Select(o => o.Name)]);
    }

    public async Task SaveRulesAsync(IReadOnlyList<ServerRule> rules, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rules);
        var (existing, others) = await ReadRulesAsync(cancellationToken);
        var wanted = rules.Where(r => r.Id is not null).Select(r => r.Id!).ToHashSet(StringComparer.Ordinal);
        var operations = new List<XElement>();
        foreach (var gone in existing.Where(e => !wanted.Contains(e.Rule.Id!)))
        {
            operations.Add(X.Typ("DeleteRuleOperation", X.Typ("RuleId", gone.Rule.Id!)));
        }

        // Neruna's rules first, in their order; Outlook's after them.
        var priority = 1;
        foreach (var rule in rules)
        {
            var known = existing.FirstOrDefault(e => e.Rule.Id == rule.Id);
            var element = RuleElement(rule, priority++);
            if (known.Rule is null)
            {
                operations.Add(X.Typ("CreateRuleOperation", element));
            }
            else if (known.Rule != rule || known.Priority != priority - 1)
            {
                operations.Add(X.Typ("SetRuleOperation", element));
            }
        }

        foreach (var other in others.OrderBy(o => o.Priority).Where(o => o.Priority < priority))
        {
            operations.Add(X.Typ("SetRuleOperation", OtherWithPriority(other.Element, priority++)));
        }

        if (operations.Count == 0)
        {
            return;
        }

        var client = await ClientAsync(cancellationToken);
        try
        {
            await client.CallSingleAsync(X.Msg("UpdateInboxRules", X.Msg("RemoveOutlookRuleBlob", "false"), X.Msg("Operations", operations)), cancellationToken);
        }
        catch (EwsException ex) when (ex.Code == "ErrorOutlookRuleBlobExists")
        {
            throw new ServerRulesUnavailableException(T("Outlook hat für dieses Postfach eigene Regeln gespeichert, die Exchange nicht anzeigt. Bitte die Regeln in Outlook oder Outlook im Web anpassen – sonst würden sie gelöscht."), ex);
        }
        catch (EwsException ex)
        {
            throw new ServerRulesUnavailableException(F("Exchange hat die Regeln abgelehnt: {0}", ex.Message), ex);
        }
    }

    private async Task<(List<(ServerRule Rule, int Priority)> Rules, List<(string Name, int Priority, XElement Element)> Others)> ReadRulesAsync(CancellationToken cancellationToken)
    {
        var client = await ClientAsync(cancellationToken);
        XElement response;
        try
        {
            response = await client.CallSingleAsync(X.Msg("GetInboxRules", X.Msg("MailboxSmtpAddress", MailboxAddress)), cancellationToken);
        }
        catch (EwsException ex)
        {
            throw new ServerRulesUnavailableException(F("Exchange gibt die Regeln nicht heraus: {0}", ex.Message), ex);
        }

        var rules = new List<(ServerRule, int)>();
        var others = new List<(string, int, XElement)>();
        foreach (var element in response.Element(X.M + "InboxRules")?.Elements(X.T + "Rule") ?? [])
        {
            var priority = int.TryParse(element.Val("Priority"), CultureInfo.InvariantCulture, out var p) ? p : int.MaxValue;
            if (ReadRule(element) is { } rule)
            {
                rules.Add((rule, priority));
            }
            else
            {
                others.Add((element.Val("DisplayName") ?? T("Regel ohne Namen"), priority, element));
            }
        }

        rules.Sort((a, b) => a.Item2.CompareTo(b.Item2));
        return (rules, others);
    }

    // A rule Neruna can show completely, or null (other conditions, exceptions or actions: left as it is).
    private static ServerRule? ReadRule(XElement element)
    {
        var conditions = element.Element(X.T + "Conditions");
        var actions = element.Element(X.T + "Actions");
        if (element.Element(X.T + "Exceptions")?.HasElements == true || actions is null
            || conditions?.Elements().Any(c => ConditionElements.All(k => k.Element != c.Name.LocalName)) == true
            || actions.Elements().Any(a => !KnownActions.Contains(a.Name.LocalName)))
        {
            return null;
        }

        var list = new List<RuleCondition>();
        foreach (var (field, name) in ConditionElements)
        {
            var strings = conditions?.Element(X.T + name)?.Elements(X.T + "String").Select(s => s.Value).ToList() ?? [];
            if (strings.Count > 1)
            {
                return null; // one value per condition in Neruna
            }

            list.AddRange(strings.Select(s => new RuleCondition(field, s)));
        }

        var forward = actions.Element(X.T + "ForwardToRecipients")?.Descendants(X.T + "EmailAddress").Select(e => e.Value).ToList() ?? [];
        return new ServerRule(
            element.Val("RuleId"),
            element.Val("DisplayName") ?? string.Empty,
            element.Val("IsEnabled") == "true",
            [.. list.OrderBy(c => c.Field)],
            actions.Element(X.T + "MoveToFolder")?.Element(X.T + "FolderId")?.Attribute("Id")?.Value,
            actions.Val("MarkAsRead") == "true",
            false,
            forward.Count == 0 ? null : string.Join(", ", forward),
            actions.Val("Delete") == "true",
            actions.Val("StopProcessingRules") == "true");
    }

    private static XElement RuleElement(ServerRule rule, int priority)
    {
        var conditions = ConditionElements
            .Select(k => (k.Element, Values: rule.Conditions.Where(c => c.Field == k.Field).Select(c => c.Value.Trim()).ToList()))
            .Where(c => c.Values.Count > 0)
            .Select(c => X.Typ(c.Element, c.Values.Select(v => X.Typ("String", v))));
        var forward = (rule.ForwardTo ?? string.Empty).Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        // Actions in the order of the schema (ActionsType).
        var actions = new List<XElement?>
        {
            rule.Delete ? X.Typ("Delete", "true") : null,
            forward.Length > 0 ? X.Typ("ForwardToRecipients", forward.Select(a => X.Typ("Address", X.Typ("EmailAddress", a)))) : null,
            rule.MarkRead ? X.Typ("MarkAsRead", "true") : null,
            rule.MoveTo is { } folder && !rule.Delete ? X.Typ("MoveToFolder", X.Typ("FolderId", new XAttribute("Id", folder))) : null,
            rule.Stop ? X.Typ("StopProcessingRules", "true") : null,
        };
        return X.Typ("Rule",
            rule.Id is null ? null : X.Typ("RuleId", rule.Id),
            X.Typ("DisplayName", rule.Name),
            X.Typ("Priority", priority.ToString(CultureInfo.InvariantCulture)),
            X.Typ("IsEnabled", rule.IsEnabled ? "true" : "false"),
            X.Typ("Conditions", conditions),
            X.Typ("Exceptions"),
            X.Typ("Actions", actions));
    }

    // A rule made elsewhere, moved behind Neruna's: unchanged except its priority.
    private static XElement OtherWithPriority(XElement rule, int priority)
    {
        var copy = new XElement(rule);
        copy.Element(X.T + "Priority")?.SetValue(priority.ToString(CultureInfo.InvariantCulture));
        foreach (var name in new[] { "IsNotSupported", "IsInError" })
        {
            copy.Element(X.T + name)?.Remove();
        }

        return copy;
    }
}
