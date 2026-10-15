using System.Text;
using System.Text.Json;
using Neruna.Core.Mail;

namespace Neruna.Providers.Imap.Sieve;

/// <summary>
/// The server rules as a block at the end of the user's active Sieve script – after the out-of-office reply, so a rule
/// that stops does not keep it from answering. As with the reply, Neruna writes only its block: filters made elsewhere
/// stay (and are named as such). The rules travel along as a comment, so Neruna reads back exactly what it wrote.
/// </summary>
internal static class SieveRules
{
    private const string Begin = "# BEGIN Neruna-Regeln";
    private const string End = "# END Neruna-Regeln";
    private const string RequireTag = "# Neruna-Regeln";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>What Neruna wrote into the script, in order.</summary>
    public static IReadOnlyList<ServerRule> Read(string? script)
    {
        foreach (var line in SieveVacation.Lines(script))
        {
            if (line.StartsWith(Begin, StringComparison.Ordinal) && line.Length > Begin.Length)
            {
                try
                {
                    return JsonSerializer.Deserialize<List<ServerRule>>(line[Begin.Length..].Trim(), Json) ?? [];
                }
                catch (JsonException)
                {
                    return []; // edited by hand: writing replaces it
                }
            }
        }

        return [];
    }

    /// <summary>Whether the script has commands besides Neruna's blocks (filters made elsewhere, e.g. in the webmail).</summary>
    public static bool HasOthers(string? script) =>
        SieveVacation.Lines(Remove(script ?? string.Empty))
            .Select(l => l.Trim())
            .Any(l => l.Length > 0 && !l.StartsWith('#') && !l.StartsWith("require", StringComparison.OrdinalIgnoreCase)
                      && !IsVacationLine(script, l));

    // Lines of Neruna's out-of-office block are no "other" filters.
    private static bool IsVacationLine(string? script, string line)
    {
        var inVacation = false;
        foreach (var l in SieveVacation.Lines(script))
        {
            if (l.StartsWith("# BEGIN Neruna-Abwesenheit", StringComparison.Ordinal))
            {
                inVacation = true;
            }
            else if (l.StartsWith("# END Neruna-Abwesenheit", StringComparison.Ordinal))
            {
                inVacation = false;
            }
            else if (inVacation && l.Trim() == line)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The Sieve extensions the rules need (the server must have them; checked before).</summary>
    public static IReadOnlyList<string> Needs(IReadOnlyList<ServerRule> rules, bool hasCopy)
    {
        var active = rules.Where(r => r.IsEnabled).ToList();
        var needs = new List<string>();
        if (active.Any(r => r.MoveTo is not null || r.Delete))
        {
            needs.Add("fileinto");
        }

        if (active.Any(r => r.MarkRead || r.Flag))
        {
            needs.Add("imap4flags");
        }

        if (active.Any(r => r.Conditions.Any(c => c.Field == RuleField.Body)))
        {
            needs.Add("body");
        }

        if (hasCopy && active.Any(r => !string.IsNullOrWhiteSpace(r.ForwardTo)))
        {
            needs.Add("copy");
        }

        return needs;
    }

    /// <param name="trash">The account's deleted items folder (for "Löschen").</param>
    /// <param name="hasCopy">The server knows ":copy" (forward and keep in one command).</param>
    public static string Write(string? script, IReadOnlyList<ServerRule> rules, string? trash, bool hasCopy)
    {
        ArgumentNullException.ThrowIfNull(rules);
        var rest = Remove(script ?? string.Empty);
        var block = new StringBuilder();
        block.Append(Begin).Append(' ').Append(JsonSerializer.Serialize(rules, Json)).Append("\r\n");
        foreach (var rule in rules.Where(r => r.IsEnabled && r.Conditions.Count > 0 && r.HasAction))
        {
            block.Append("# ").Append(rule.Name.Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal)).Append("\r\n");
            var tests = rule.Conditions.Select(Test).ToList();
            block.Append("if ").Append(tests.Count == 1 ? tests[0] : $"allof ({string.Join(", ", tests)})").Append(" {\r\n");
            if (rule.MarkRead)
            {
                block.Append("  addflag \"\\\\Seen\";\r\n");
            }

            if (rule.Flag)
            {
                block.Append("  addflag \"\\\\Flagged\";\r\n");
            }

            if (!string.IsNullOrWhiteSpace(rule.ForwardTo))
            {
                foreach (var address in rule.ForwardTo.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    block.Append(hasCopy ? "  redirect :copy " : "  redirect ").Append(SieveVacation.Quote(address)).Append(";\r\n");
                }

                if (!hasCopy && rule.MoveTo is null && !rule.Delete)
                {
                    block.Append("  keep;\r\n"); // without :copy a redirect would take the mail away
                }
            }

            var target = rule.Delete ? trash : rule.MoveTo;
            if (target is not null)
            {
                block.Append("  fileinto ").Append(SieveVacation.Quote(target)).Append(";\r\n");
            }

            if (rule.Stop)
            {
                block.Append("  stop;\r\n");
            }

            block.Append("}\r\n");
        }

        block.Append(End).Append("\r\n");

        var needs = Needs(rules, hasCopy);
        var result = new StringBuilder();
        if (needs.Count > 0)
        {
            result.Append("require [").Append(string.Join(", ", needs.Select(n => $"\"{n}\""))).Append("]; ").Append(RequireTag).Append("\r\n");
        }

        result.Append(rest);
        if (rest.Length > 0 && !rest.EndsWith('\n'))
        {
            result.Append("\r\n");
        }

        result.Append(block);
        return SieveVacation.Normalize(result.ToString());
    }

    private static string Test(RuleCondition condition)
    {
        var value = SieveVacation.Quote(condition.Value.Trim());
        return condition.Field switch
        {
            RuleField.From => $"address :contains \"from\" {value}",
            RuleField.Recipient => $"address :contains [\"to\", \"cc\"] {value}",
            RuleField.Subject => $"header :contains \"subject\" {value}",
            _ => $"body :text :contains {value}",
        };
    }

    // The script without Neruna's rules block and its require line.
    private static string Remove(string script)
    {
        var result = new StringBuilder();
        var inBlock = false;
        foreach (var line in SieveVacation.Lines(script))
        {
            if (line.StartsWith(Begin, StringComparison.Ordinal))
            {
                inBlock = true;
            }
            else if (inBlock && line.StartsWith(End, StringComparison.Ordinal))
            {
                inBlock = false;
            }
            else if (!inBlock && !line.TrimEnd().EndsWith(RequireTag, StringComparison.Ordinal))
            {
                result.Append(line).Append("\r\n");
            }
        }

        return result.ToString();
    }
}
