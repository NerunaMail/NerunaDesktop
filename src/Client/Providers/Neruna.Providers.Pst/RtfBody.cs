using System.Globalization;
using System.Text;

namespace Neruna.Providers.Pst;

/// <summary>
/// Message bodies that Outlook kept only as RTF: the HTML it wrapped in RTF ("\fromhtml1", [MS-OXRTFEX]) comes out
/// as it was; other RTF becomes plain text. Pictures, tables and fonts of real RTF documents are not kept.
/// </summary>
internal static class RtfBody
{
    static RtfBody() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    /// <summary>The HTML encapsulated in <paramref name="rtf"/>, or null when it is no wrapped HTML.</summary>
    public static string? HtmlOf(string? rtf) =>
        rtf is not null && rtf.Contains(@"\fromhtml", StringComparison.Ordinal) ? Convert(rtf, html: true) : null;

    /// <summary>The text of <paramref name="rtf"/> (wrapped HTML or text included), without formatting.</summary>
    public static string? TextOf(string? rtf) =>
        rtf is null || !rtf.TrimStart().StartsWith(@"{\rtf", StringComparison.Ordinal) ? null : Convert(rtf, html: false).Trim();

    // Destinations whose content is not text (font and colour tables, pictures, document info …).
    private static readonly HashSet<string> SkippedDestinations = new(StringComparer.Ordinal)
    {
        "fonttbl", "colortbl", "stylesheet", "info", "pict", "object", "header", "footer", "headerl", "headerr", "footerl",
        "footerr", "xmlnstbl", "listtable", "listoverridetable", "rsidtbl", "generator", "themedata", "colorschememapping",
        "latentstyles", "datastore", "fldinst", "filetbl", "revtbl", "pgdsctbl", "mmathPr", "defchp", "defpap",
    };

    private sealed class State
    {
        public bool Skip;

        // Inside {\*\htmltag …}: the original HTML (output even where \htmlrtf suppresses the RTF rendering).
        public bool HtmlTag;

        // Between \htmlrtf and \htmlrtf0: RTF made only for RTF readers – not part of the HTML.
        public bool HtmlRtf;

        public int UnicodeSkip = 1;

        public State Copy() => (State)MemberwiseClone();
    }

    private static string Convert(string rtf, bool html)
    {
        var output = new StringBuilder(rtf.Length / 2);
        var bytes = new List<byte>();
        var encoding = Encoding.Latin1;
        var stack = new Stack<State>();
        var state = new State();
        var skipChars = 0;
        var i = 0;

        bool Emits() => !state.Skip && (state.HtmlTag || !html || !state.HtmlRtf);

        void Flush()
        {
            if (bytes.Count > 0)
            {
                output.Append(encoding.GetString(bytes.ToArray()));
                bytes.Clear();
            }
        }

        void Text(string text)
        {
            Flush();
            if (Emits())
            {
                output.Append(text);
            }
        }

        while (i < rtf.Length)
        {
            var c = rtf[i];
            switch (c)
            {
                case '{':
                    Flush();
                    stack.Push(state);
                    state = state.Copy();
                    i++;
                    break;
                case '}':
                    Flush();
                    state = stack.Count > 0 ? stack.Pop() : state;
                    i++;
                    break;
                case '\\':
                    i = ControlWord(rtf, i, out var word, out var parameter, out var symbol);
                    if (symbol is { } literal)
                    {
                        if (literal == '\'')
                        {
                            // \'hh: a byte in the document's code page (several make one character in Asian code pages).
                            if (i + 1 < rtf.Length && byte.TryParse(rtf.AsSpan(i, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value))
                            {
                                i += 2;
                                if (skipChars > 0)
                                {
                                    skipChars--;
                                }
                                else if (Emits())
                                {
                                    bytes.Add(value);
                                }
                            }
                        }
                        else if (literal == '*')
                        {
                            // {\*\name …}: ignorable unless it is the wrapped HTML.
                            var next = ControlWord(rtf, i, out var name, out _, out _);
                            if (name == "htmltag")
                            {
                                state.HtmlTag = true;
                                i = next;
                                SkipSpace(rtf, ref i);
                            }
                            else
                            {
                                state.Skip = true;
                            }
                        }
                        else if (literal is '\\' or '{' or '}')
                        {
                            Text(literal.ToString());
                        }
                        else if (literal == '~')
                        {
                            Text(" ");
                        }
                        else if (literal is '\r' or '\n')
                        {
                            Text("\r\n");
                        }

                        break;
                    }

                    switch (word)
                    {
                        case "ansicpg" when parameter is { } codePage:
                            try
                            {
                                encoding = Encoding.GetEncoding(codePage);
                            }
                            catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
                            {
                                encoding = Encoding.Latin1;
                            }

                            break;
                        case "htmlrtf":
                            Flush();
                            state.HtmlRtf = parameter != 0;
                            break;
                        case "uc" when parameter is { } count:
                            state.UnicodeSkip = count;
                            break;
                        case "u" when parameter is { } code:
                            Text(((char)(code < 0 ? code + 65536 : code)).ToString());
                            skipChars = state.UnicodeSkip;
                            break;
                        case "par" or "line":
                            Text("\r\n");
                            break;
                        case "tab":
                            Text("\t");
                            break;
                        case "emdash":
                            Text("—");
                            break;
                        case "endash":
                            Text("–");
                            break;
                        case "lquote":
                            Text("‘");
                            break;
                        case "rquote":
                            Text("’");
                            break;
                        case "ldblquote":
                            Text("“");
                            break;
                        case "rdblquote":
                            Text("”");
                            break;
                        case "bullet":
                            Text("•");
                            break;
                        default:
                            if (word is not null && SkippedDestinations.Contains(word))
                            {
                                state.Skip = true;
                            }

                            break;
                    }

                    break;
                case '\r' or '\n':
                    i++;
                    break;
                default:
                    if (skipChars > 0)
                    {
                        skipChars--;
                    }
                    else if (Emits())
                    {
                        Flush();
                        output.Append(c);
                    }

                    i++;
                    break;
            }
        }

        Flush();
        return output.ToString();
    }

    private static void SkipSpace(string rtf, ref int i)
    {
        if (i < rtf.Length && rtf[i] == ' ')
        {
            i++;
        }
    }

    /// <summary>Reads "\word123 " or "\x" at <paramref name="start"/>; returns the index after it (a delimiting space is eaten).</summary>
    private static int ControlWord(string rtf, int start, out string? word, out int? parameter, out char? symbol)
    {
        word = null;
        parameter = null;
        symbol = null;
        var i = start + 1;
        if (i >= rtf.Length)
        {
            return i;
        }

        if (!char.IsAsciiLetter(rtf[i]))
        {
            symbol = rtf[i];
            return i + 1;
        }

        var begin = i;
        while (i < rtf.Length && char.IsAsciiLetter(rtf[i]))
        {
            i++;
        }

        word = rtf[begin..i];
        var number = i;
        if (i < rtf.Length && (rtf[i] == '-' || char.IsAsciiDigit(rtf[i])))
        {
            i++;
            while (i < rtf.Length && char.IsAsciiDigit(rtf[i]))
            {
                i++;
            }

            if (int.TryParse(rtf.AsSpan(number, i - number), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value))
            {
                parameter = value;
            }
        }

        SkipSpace(rtf, ref i);
        return i;
    }
}
