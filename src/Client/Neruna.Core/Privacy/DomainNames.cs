using System.Globalization;
using System.Reflection;
using System.Text;

namespace Neruna.Core.Privacy;

/// <summary>
/// Host names as a person should compare them: the registrable domain ("login.bank.co.uk" → "bank.co.uk", from the
/// Public Suffix List), the ASCII form (Punycode) and whether the name mixes scripts or imitates another one.
/// </summary>
public static class DomainNames
{
    private static readonly Lazy<SuffixList> Suffixes = new(SuffixList.Load);
    private static readonly IdnMapping Idn = new() { AllowUnassigned = true };

    /// <summary>"Bücher.example" → "xn--bcher-kva.example", lower case and without a trailing dot; null if invalid.</summary>
    public static string? ToAscii(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return null;
        }

        host = host.Trim().TrimEnd('.');
        try
        {
            return Idn.GetAscii(host).ToLowerInvariant();
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>"xn--bcher-kva.example" → "bücher.example" (as it is shown); the input if it cannot be decoded.</summary>
    public static string ToUnicode(string host)
    {
        ArgumentNullException.ThrowIfNull(host);
        try
        {
            return Idn.GetUnicode(host);
        }
        catch (ArgumentException)
        {
            return host;
        }
    }

    /// <summary>A name in Punycode (some label starts with "xn--"): it shows as other characters than it consists of.</summary>
    public static bool IsInternational(string? host) =>
        ToAscii(host) is { } ascii && ascii.Split('.').Any(l => l.StartsWith("xn--", StringComparison.Ordinal));

    /// <summary>An IPv4 or IPv6 address instead of a name.</summary>
    public static bool IsIpAddress(string? host) =>
        host is not null && System.Net.IPAddress.TryParse(host.Trim('[', ']'), out _);

    /// <summary>
    /// The part of the name its owner registered: "mail.google.com" → "google.com", "shop.example.co.uk" →
    /// "example.co.uk", "user.github.io" → "user.github.io". Null for IP addresses, suffixes alone and invalid names.
    /// </summary>
    public static string? Registrable(string? host)
    {
        var ascii = ToAscii(host);
        if (ascii is null || IsIpAddress(ascii))
        {
            return null;
        }

        var labels = ascii.Split('.');
        var suffix = Suffixes.Value.SuffixLength(labels);
        return suffix >= labels.Length || suffix == 0 ? null : string.Join('.', labels[^(suffix + 1)..]);
    }

    /// <summary>Both names belong to the same registrable domain (or are equal, if that cannot be determined).</summary>
    public static bool SameOwner(string? a, string? b)
    {
        var ra = Registrable(a) ?? ToAscii(a);
        var rb = Registrable(b) ?? ToAscii(b);
        return ra is not null && string.Equals(ra, rb, StringComparison.Ordinal);
    }

    /// <summary>The domain of an address ("anna@Example.ch" → "example.ch"); null without "@".</summary>
    public static string? OfAddress(string? address)
    {
        var at = address?.LastIndexOf('@') ?? -1;
        return at < 0 ? null : ToAscii(address![(at + 1)..]);
    }

    /// <summary>
    /// The name mixes scripts within one label ("pаypal" with a Cyrillic "а"), the classic homograph trick. Names in
    /// one script only (all Cyrillic, all Greek …) are fine.
    /// </summary>
    public static bool IsMixedScript(string? host)
    {
        if (host is null)
        {
            return false;
        }

        foreach (var label in ToUnicode(host).Split('.'))
        {
            var scripts = new HashSet<Script>();
            foreach (var rune in label.EnumerateRunes())
            {
                if (ScriptOf(rune) is { } script)
                {
                    scripts.Add(script);
                }
            }

            // Han, Hiragana and Katakana are used together in Japanese; Latin with them is common for brands.
            scripts.ExceptWith([Script.Han, Script.Kana]);
            if (scripts.Count > 1)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// <paramref name="candidate"/> looks like <paramref name="original"/> without being it: one or two letters
    /// different, swapped or doubled ("paypa1.com", "rnicrosoft.com", "neruna-mail.ch" is not caught), or the same
    /// after mapping look-alike characters. Compares the registrable domains; short names (under 5 characters) only
    /// by look-alikes, a distance of one is too common there.
    /// </summary>
    public static bool LooksLike(string candidate, string original)
    {
        var a = Registrable(candidate) ?? ToAscii(candidate);
        var b = Registrable(original) ?? ToAscii(original);
        if (a is null || b is null || a == b)
        {
            return false;
        }

        var sa = Skeleton(ToUnicode(a));
        var sb = Skeleton(ToUnicode(b));
        if (sa == sb)
        {
            return true;
        }

        // The name without its suffix: "paypal" of "paypal.com"; same name under another suffix is no typo.
        var na = sa[..Math.Max(sa.IndexOf('.'), 0)];
        var nb = sb[..Math.Max(sb.IndexOf('.'), 0)];
        if (na.Length < 5 || nb.Length < 5)
        {
            return false;
        }

        if (na == nb)
        {
            return false;
        }

        var distance = Distance(na, nb);
        return distance == 1 || (distance == 2 && Math.Min(na.Length, nb.Length) >= 8);
    }

    // Characters that look alike in common fonts, mapped to one form (Unicode TR 39 "skeleton", in small).
    private static string Skeleton(string text)
    {
        var builder = new System.Text.StringBuilder(text.Length);
        foreach (var rune in text.Normalize(System.Text.NormalizationForm.FormKD).EnumerateRunes())
        {
            if (Rune.GetUnicodeCategory(rune) == UnicodeCategory.NonSpacingMark)
            {
                continue; // é → e
            }

            builder.Append(rune.Value switch
            {
                '0' or 'о' or 'ο' => "o",
                '1' or 'l' or 'ӏ' or 'ı' or 'і' or '|' => "l",
                'а' or 'α' => "a",
                'е' or 'ε' => "e",
                'р' or 'ρ' => "p",
                'с' => "c",
                'х' or 'χ' => "x",
                'у' => "y",
                'ѕ' => "s",
                'ј' => "j",
                'ԁ' => "d",
                'ν' => "v",
                'к' or 'κ' => "k",
                'н' => "h",
                'т' or 'τ' => "t",
                'в' => "b",
                'м' => "m",
                _ => rune.ToString().ToLowerInvariant(),
            });
        }

        // Two letters that read as one: "rn" → "m", "vv" → "w", "cl" → "d".
        return builder.ToString().Replace("rn", "m", StringComparison.Ordinal).Replace("vv", "w", StringComparison.Ordinal).Replace("cl", "d", StringComparison.Ordinal);
    }

    // Damerau-Levenshtein (with swapped neighbours as one step).
    private static int Distance(string a, string b)
    {
        var d = new int[a.Length + 1, b.Length + 1];
        for (var i = 0; i <= a.Length; i++)
        {
            d[i, 0] = i;
        }

        for (var j = 0; j <= b.Length; j++)
        {
            d[0, j] = j;
        }

        for (var i = 1; i <= a.Length; i++)
        {
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
                if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1])
                {
                    d[i, j] = Math.Min(d[i, j], d[i - 2, j - 2] + 1);
                }
            }
        }

        return d[a.Length, b.Length];
    }

    private enum Script
    {
        Latin,
        Greek,
        Cyrillic,
        Armenian,
        Hebrew,
        Arabic,
        Han,
        Kana,
        Hangul,
        Thai,
        Other,
    }

    // Digits, hyphens and other common characters belong to no script.
    private static Script? ScriptOf(Rune rune)
    {
        var c = rune.Value;
        if (c < 0x80)
        {
            return char.IsAsciiLetter((char)c) ? Script.Latin : null;
        }

        if (!Rune.IsLetter(rune))
        {
            return null;
        }

        return c switch
        {
            <= 0x024F or (>= 0x1E00 and <= 0x1EFF) => Script.Latin,
            >= 0x0370 and <= 0x03FF or (>= 0x1F00 and <= 0x1FFF) => Script.Greek,
            >= 0x0400 and <= 0x052F => Script.Cyrillic,
            >= 0x0530 and <= 0x058F => Script.Armenian,
            >= 0x0590 and <= 0x05FF => Script.Hebrew,
            >= 0x0600 and <= 0x06FF or (>= 0x0750 and <= 0x077F) => Script.Arabic,
            >= 0x0E00 and <= 0x0E7F => Script.Thai,
            >= 0x3040 and <= 0x30FF => Script.Kana,
            >= 0x4E00 and <= 0x9FFF or (>= 0x3400 and <= 0x4DBF) => Script.Han,
            >= 0xAC00 and <= 0xD7AF or (>= 0x1100 and <= 0x11FF) => Script.Hangul,
            _ => Script.Other,
        };
    }

    /// <summary>The Public Suffix List (MPL 2.0, embedded), with its rules in ASCII.</summary>
    private sealed class SuffixList
    {
        private readonly HashSet<string> _rules = new(StringComparer.Ordinal);
        private readonly HashSet<string> _wildcards = new(StringComparer.Ordinal);
        private readonly HashSet<string> _exceptions = new(StringComparer.Ordinal);

        public static SuffixList Load()
        {
            var list = new SuffixList();
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Neruna.Core.Privacy.public_suffix_list.dat");
            if (stream is null)
            {
                return list;
            }

            using var reader = new StreamReader(stream);
            while (reader.ReadLine() is { } line)
            {
                line = line.Trim();
                if (line.Length == 0 || line.StartsWith("//", StringComparison.Ordinal))
                {
                    continue;
                }

                if (line.StartsWith('!'))
                {
                    list._exceptions.Add(ToAscii(line[1..]) ?? line[1..]);
                }
                else if (line.StartsWith("*.", StringComparison.Ordinal))
                {
                    list._wildcards.Add(ToAscii(line[2..]) ?? line[2..]);
                }
                else
                {
                    list._rules.Add(ToAscii(line) ?? line);
                }
            }

            return list;
        }

        /// <summary>How many labels at the end are the public suffix (an unknown TLD counts as one, as the list says).</summary>
        public int SuffixLength(string[] labels)
        {
            for (var start = 0; start < labels.Length; start++)
            {
                var name = string.Join('.', labels[start..]);
                if (_exceptions.Contains(name))
                {
                    return labels.Length - start - 1;
                }

                if (_rules.Contains(name))
                {
                    return labels.Length - start;
                }

                if (start + 1 < labels.Length && _wildcards.Contains(string.Join('.', labels[(start + 1)..])))
                {
                    return labels.Length - start;
                }
            }

            return 1;
        }
    }
}
