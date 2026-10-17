using System.Globalization;
using System.Text.RegularExpressions;
using MimeKit;
using static Neruna.Core.Localization.Texts;

namespace Neruna.Core.Privacy;

/// <summary>Reads the spam filter's rating from the headers the receiving mail server added (the topmost of each kind).</summary>
public static partial class SpamRatings
{
    [GeneratedRegex(@"score=(-?[\d.]+)\s+required=(-?[\d.]+)", RegexOptions.IgnoreCase)]
    private static partial Regex SpamAssassin();

    [GeneratedRegex(@"\[\s*(-?[\d.]+)\s*/\s*(-?[\d.]+)\s*\]")]
    private static partial Regex Rspamd();

    [GeneratedRegex(@"(\d{1,3})\s*%")]
    private static partial Regex Percent();

    [GeneratedRegex(@"(?:^|;)\s*SCL:\s*(-?\d)", RegexOptions.IgnoreCase)]
    private static partial Regex Scl();

    public static SpamRating? Of(MimeMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        string? Header(string name) => message.Headers.FirstOrDefault(h => h.Field.Equals(name, StringComparison.OrdinalIgnoreCase))?.Value;
        static double Number(string value) => double.Parse(value, CultureInfo.InvariantCulture);

        // SpamAssassin (also many hosters): "Yes, score=5.8 required=6.0 tests=…"
        if (Header("X-Spam-Status") is { } status && SpamAssassin().Match(status) is { Success: true } sa)
        {
            var (score, threshold) = (Number(sa.Groups[1].Value), Number(sa.Groups[2].Value));
            return Rating("SpamAssassin", score, threshold, status.TrimStart().StartsWith("yes", StringComparison.OrdinalIgnoreCase) || score >= threshold);
        }

        // Rspamd: "default: False [5.80 / 15.00]; …"
        if (Header("X-Spamd-Result") is { } rspamd && Rspamd().Match(rspamd) is { Success: true } rs)
        {
            var (score, threshold) = (Number(rs.Groups[1].Value), Number(rs.Groups[2].Value));
            return Rating("Rspamd", score, threshold, rspamd.Contains("true", StringComparison.OrdinalIgnoreCase) || score >= threshold);
        }

        // Sophos: "X-SASI-SpamProbability: 10%"
        if (Header("X-SASI-SpamProbability") is { } sophos && Percent().Match(sophos) is { Success: true } sp)
        {
            var percent = int.Parse(sp.Groups[1].Value, CultureInfo.InvariantCulture);
            return new SpamRating("Sophos", percent, 100, percent >= 50, F("{0} % Spamwahrscheinlichkeit ({1})", percent, "Sophos"));
        }

        // Microsoft 365 / Exchange: spam confidence level 0–9, from 5 on junk.
        if ((Header("X-MS-Exchange-Organization-SCL") ?? (Header("X-Forefront-Antispam-Report") is { } report && Scl().Match(report) is { Success: true } m ? m.Groups[1].Value : null))
            is { } scl && int.TryParse(scl.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var level) && level >= 0)
        {
            return new SpamRating("Microsoft", level, 5, level >= 5, F("Spam-Stufe {0} von 9, ab 5 Spam ({1})", level, "Microsoft"));
        }

        // Only a flag or a score without threshold.
        if (Header("X-Spam-Flag") is { } flag)
        {
            var yes = flag.Trim().StartsWith("yes", StringComparison.OrdinalIgnoreCase);
            return new SpamRating(T("Mailserver"), null, null, yes, yes ? T("als Spam markiert") : T("nicht als Spam markiert"));
        }

        if ((Header("X-Spam-Score") ?? Header("X-Rspamd-Score")) is { } plain && double.TryParse(plain.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var only))
        {
            return new SpamRating(T("Mailserver"), only, null, false, F("{0:0.#} Punkte (ohne bekannte Schwelle)", only));
        }

        return null;
    }

    private static SpamRating Rating(string filter, double score, double threshold, bool spam) =>
        new(filter, score, threshold, spam, F("{0:0.#} von {1:0.#} Punkten ({2}) – {3}", score, threshold, filter,
            spam ? T("über der Spam-Schwelle") : score >= threshold * 0.8 ? T("knapp unter der Spam-Schwelle") : T("unter der Spam-Schwelle")));
}
