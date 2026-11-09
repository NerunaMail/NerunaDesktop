using System.Globalization;
using System.Text;
using System.Xml.Linq;
using Neruna.Core.Calendar;
using static Neruna.Core.Localization.Texts;

namespace Neruna.Providers.Ews;

/// <summary>
/// The mailbox's category list (when the account stores colours on the server): Exchange keeps it as the user
/// configuration "CategoryList" of the calendar folder – an XML list with a colour number (0 … 24) per category, which
/// Outlook reads too. Only names and colours change; all else in the list stays as Outlook wrote it.
/// </summary>
internal sealed partial class EwsCalendarProvider : ICalendarCategoryColors
{
    private static readonly XNamespace CategoryNs = "CategoryList.xsd";

    public async Task<IReadOnlyList<CalendarCategory>> GetCategoriesAsync(CancellationToken cancellationToken = default)
    {
        var list = await LoadCategoryListAsync(cancellationToken);
        return list is null
            ? []
            : [.. list.Elements(CategoryNs + "category")
                .Where(c => !string.IsNullOrWhiteSpace((string?)c.Attribute("name")))
                .Select(c => new CalendarCategory((string)c.Attribute("name")!,
                    OutlookColors.CategoryColor(int.TryParse((string?)c.Attribute("color"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i : -1)
                    ?? OutlookColors.Categories[12]))];
    }

    public async Task SetCategoryColorAsync(string name, string color, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var list = await LoadCategoryListAsync(cancellationToken)
                   ?? throw new NotSupportedException(T("Dieses Postfach hat noch keine Kategorienliste – bitte einmal im Webmail eine Kategorie anlegen."));
        var index = OutlookColors.NearestCategory(color).ToString(CultureInfo.InvariantCulture);
        var category = list.Elements(CategoryNs + "category").FirstOrDefault(c => string.Equals((string?)c.Attribute("name"), name.Trim(), StringComparison.OrdinalIgnoreCase));
        if (category is not null)
        {
            if ((string?)category.Attribute("color") == index)
            {
                return;
            }

            category.SetAttributeValue("color", index);
        }
        else
        {
            list.Add(new XElement(CategoryNs + "category",
                new XAttribute("name", name.Trim()), new XAttribute("color", index), new XAttribute("keyboardShortcut", "0"),
                new XAttribute("usageCount", "0"), new XAttribute("guid", "{" + Guid.NewGuid().ToString().ToUpperInvariant() + "}"),
                new XAttribute("renameOnFirstUse", "0")));
        }

        var client = await ClientAsync(cancellationToken);
        var data = Convert.ToBase64String(Encoding.UTF8.GetBytes(list.ToString(SaveOptions.DisableFormatting)));
        await client.CallSingleAsync(X.Msg("UpdateUserConfiguration",
            X.Msg("UserConfiguration",
                X.Typ("UserConfigurationName", new XAttribute("Name", "CategoryList"), X.FolderId("calendar")),
                X.Typ("XmlData", data))), cancellationToken);
    }

    /// <returns>The &lt;categories&gt; element; null if the mailbox has none yet (Outlook was never opened).</returns>
    private async Task<XElement?> LoadCategoryListAsync(CancellationToken cancellationToken)
    {
        var client = await ClientAsync(cancellationToken);
        XElement response;
        try
        {
            response = await client.CallSingleAsync(X.Msg("GetUserConfiguration",
                X.Msg("UserConfigurationName", new XAttribute("Name", "CategoryList"), X.FolderId("calendar")),
                X.Msg("UserConfigurationProperties", "XmlData")), cancellationToken);
        }
        catch (EwsException ex) when (ex.Code is "ErrorItemNotFound")
        {
            return null;
        }

        return response.Descendants(X.T + "XmlData").FirstOrDefault()?.Value is { Length: > 0 } data
            ? XDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(data)).TrimStart('﻿')).Root
            : null;
    }
}
