using System.Globalization;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;

namespace Neruna.Desktop.Infrastructure;

/// <summary>
/// Flexbox and grid for the mail renderer, which knows neither: the elements a mail lays out that way (its style
/// blocks' selectors or a style attribute) are rewritten into what the renderer does know, so the mail looks as the
/// sender meant it. A column ("flex-direction: column") puts its parts one below the other – a table row becomes one
/// row per cell; a row puts them side by side in a one-row table, aligned and spaced as the flexbox said. The
/// declarations themselves are taken out afterwards (MailPaper).
/// </summary>
internal static partial class FlexLayout
{
    public static string Apply(string html)
    {
        if (!FlexDisplay().IsMatch(html))
        {
            return html;
        }

        try
        {
            var document = new HtmlParser().ParseDocument(html);
            var targets = new List<(IElement Element, string Declarations)>();
            foreach (var style in document.QuerySelectorAll("style"))
            {
                foreach (Match rule in Rule().Matches(style.TextContent))
                {
                    var declarations = rule.Groups["body"].Value;
                    if (!FlexDisplay().IsMatch(declarations))
                    {
                        continue;
                    }

                    foreach (var part in rule.Groups["selector"].Value.Split(','))
                    {
                        // States (":hover") never apply to a mail on screen; at-rules are no selectors.
                        var selector = part.Trim();
                        if (selector.Length == 0 || selector.StartsWith('@') || selector.Contains(':', StringComparison.Ordinal))
                        {
                            continue;
                        }

                        try
                        {
                            targets.AddRange(document.QuerySelectorAll(selector).Select(e => (e, declarations)));
                        }
                        catch (DomException)
                        {
                            // A selector the parser does not know: that rule stays without effect, as in many mail programs.
                        }
                    }
                }
            }

            // The element's own style comes last and wins.
            targets.AddRange(document.QuerySelectorAll("[style]")
                .Where(e => FlexDisplay().IsMatch(e.GetAttribute("style") ?? string.Empty))
                .Select(e => (e, e.GetAttribute("style") ?? string.Empty)));
            if (targets.Count == 0)
            {
                return html;
            }

            // Inner elements first: a row inside a column is laid out before the column moves it.
            foreach (var group in targets.GroupBy(t => t.Element).OrderByDescending(g => Depth(g.Key)))
            {
                Rewrite(group.Key, string.Join(";", group.Select(t => t.Declarations)));
            }

            return document.DocumentElement.OuterHtml;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return html; // the mail as it came is better than none
        }
    }

    private static int Depth(IElement element)
    {
        var depth = 0;
        for (var parent = element.ParentElement; parent is not null; parent = parent.ParentElement)
        {
            depth++;
        }

        return depth;
    }

    private static void Rewrite(IElement element, string declarations)
    {
        // The last display wins (a later rule may set block again).
        if (Display().Matches(declarations).LastOrDefault()?.Groups["value"].Value.ToLowerInvariant() is not ("flex" or "inline-flex" or "grid" or "inline-grid"))
        {
            return;
        }

        var grid = Display().Matches(declarations)[^1].Groups["value"].Value.Contains("grid", StringComparison.OrdinalIgnoreCase);
        var column = grid || Direction().Matches(declarations).LastOrDefault()?.Groups["value"].Value.StartsWith("column", StringComparison.OrdinalIgnoreCase) == true;
        var gap = Gap().Matches(declarations).LastOrDefault() is { } g ? double.Parse(g.Groups["value"].Value, CultureInfo.InvariantCulture) : 0;
        var centered = AlignCenter().IsMatch(declarations);
        if (column)
        {
            Column(element, gap, centered);
        }
        else
        {
            Row(element, gap, centered, Justify().Matches(declarations).LastOrDefault()?.Groups["value"].Value.ToLowerInvariant());
        }
    }

    // One below the other: a table row becomes a row per cell; anything else gets its parts as blocks.
    private static void Column(IElement element, double gap, bool centered)
    {
        var document = element.Owner!;
        if (element.LocalName == "tr")
        {
            var cells = element.Children.Where(c => c.LocalName is "td" or "th").ToList();
            var after = element;
            for (var i = 0; i < cells.Count; i++)
            {
                var cell = cells[i];
                AddStyle(cell, (centered ? "text-align: center;" : string.Empty) + (i > 0 && gap > 0 ? Px("padding-top", gap) : string.Empty));
                if (i == 0)
                {
                    continue;
                }

                var row = (IElement)element.Clone(deep: false);
                row.AppendChild(cell);
                after.InsertAfter(row);
                after = row;
            }

            return;
        }

        var parts = Parts(element);
        for (var i = 0; i < parts.Count; i++)
        {
            var block = document.CreateElement("div");
            AddStyle(block, (centered ? "text-align: center;" : string.Empty) + (i > 0 && gap > 0 ? Px("margin-top", gap) : string.Empty));
            element.InsertBefore(block, parts[i]);
            block.AppendChild(parts[i]);
            Unwrap(parts[i]);
        }
    }

    // Side by side: a one-row table; a row of a table already is one.
    private static void Row(IElement element, double gap, bool centered, string? justify)
    {
        if (element.LocalName is "tr" or "table" or "tbody" or "thead" or "tfoot")
        {
            if (centered && element.LocalName == "tr")
            {
                foreach (var cell in element.Children.Where(c => c.LocalName is "td" or "th"))
                {
                    AddStyle(cell, "vertical-align: middle;");
                }
            }

            return;
        }

        var parts = Parts(element);
        if (parts.Count < 2)
        {
            return;
        }

        var document = element.Owner!;
        var table = document.CreateElement("table");
        table.SetAttribute("cellpadding", "0");
        table.SetAttribute("cellspacing", "0");
        table.SetAttribute("border", "0");
        AddStyle(table, justify switch
        {
            "center" => "margin-left: auto; margin-right: auto;",
            "space-between" or "space-around" or "space-evenly" => "width: 100%;",
            "flex-end" or "end" or "right" => "margin-left: auto;",
            _ => string.Empty,
        });
        var row = document.CreateElement("tr");
        table.AppendChild(row);
        element.InsertBefore(table, parts[0]);
        for (var i = 0; i < parts.Count; i++)
        {
            var cell = document.CreateElement("td");
            AddStyle(cell, (centered ? "vertical-align: middle;" : "vertical-align: top;")
                           + (i < parts.Count - 1 && gap > 0 ? Px("padding-right", gap) : string.Empty)
                           + (justify is "space-between" && i == parts.Count - 1 && parts.Count > 1 ? "text-align: right;" : string.Empty));
            row.AppendChild(cell);
            cell.AppendChild(parts[i]);
            Unwrap(parts[i]);
        }
    }

    /// <summary>The flex items: child elements, and text that is more than white space (in a span of its own).</summary>
    private static List<INode> Parts(IElement element)
    {
        var parts = new List<INode>();
        foreach (var node in element.ChildNodes.ToList())
        {
            if (node is IElement)
            {
                parts.Add(node);
            }
            else if (node is IText text && !string.IsNullOrWhiteSpace(text.Data))
            {
                var span = element.Owner!.CreateElement("span");
                element.InsertBefore(span, node);
                span.AppendChild(node);
                parts.Add(span);
            }
        }

        return parts;
    }

    // A list item moved out of its list would show a bullet or nothing: its content goes in its place.
    private static void Unwrap(INode part)
    {
        if (part is IElement { LocalName: "li" } item && item.Parent is { } parent)
        {
            foreach (var child in item.ChildNodes.ToList())
            {
                parent.InsertBefore(child, item);
            }

            item.Remove();
        }
    }

    private static void AddStyle(IElement element, string style)
    {
        if (style.Length == 0)
        {
            return;
        }

        var own = element.GetAttribute("style")?.Trim().TrimEnd(';');
        element.SetAttribute("style", string.IsNullOrEmpty(own) ? style : own + "; " + style);
    }

    private static string Px(string property, double value) => string.Create(CultureInfo.InvariantCulture, $"{property}: {value}px;");

    [GeneratedRegex(@"display\s*:\s*(inline-)?(flex|grid)", RegexOptions.IgnoreCase)]
    private static partial Regex FlexDisplay();

    [GeneratedRegex(@"(?<selector>[^{}]+)\{(?<body>[^{}]*)\}")]
    private static partial Regex Rule();

    [GeneratedRegex(@"(?<![-\w])display\s*:\s*(?<value>[\w-]+)", RegexOptions.IgnoreCase)]
    private static partial Regex Display();

    [GeneratedRegex(@"flex-direction\s*:\s*(?<value>[\w-]+)", RegexOptions.IgnoreCase)]
    private static partial Regex Direction();

    [GeneratedRegex(@"(?<![-\w])(row-)?gap\s*:\s*(?<value>\d+(\.\d+)?)px", RegexOptions.IgnoreCase)]
    private static partial Regex Gap();

    [GeneratedRegex(@"align-items\s*:\s*center", RegexOptions.IgnoreCase)]
    private static partial Regex AlignCenter();

    [GeneratedRegex(@"justify-content\s*:\s*(?<value>[\w-]+)", RegexOptions.IgnoreCase)]
    private static partial Regex Justify();
}
