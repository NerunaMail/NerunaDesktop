using System.Globalization;
using System.Xml.Linq;

namespace Neruna.Providers.Ews;

/// <summary>Requests that mail, calendar and contacts share: folders, listing item ids, loading items.</summary>
internal static class EwsOperations
{
    /// <summary>MAPI PR_ATTR_HIDDEN: folders Outlook does not show (settings, sync state …).</summary>
    private const int HiddenTag = 0x10F4;

    /// <summary>
    /// Every folder below the mailbox root (deep), with name, parent, class, counts and – where useful – rights. Hidden
    /// folders and search folders are left out. The id of the root itself comes first.
    /// </summary>
    public static async Task<(string RootId, IReadOnlyList<XElement> Folders)> FindFoldersAsync(EwsClient client, CancellationToken cancellationToken, params string[] extraFields)
    {
        var root = (await client.CallAsync(X.Msg("GetFolder",
            X.Msg("FolderShape", X.Typ("BaseShape", "IdOnly")),
            X.Msg("FolderIds", X.FolderId("msgfolderroot"))), cancellationToken)).Single();
        var rootId = root.Descendants(X.T + "FolderId").First().Attribute("Id")!.Value;

        var folders = new List<XElement>();
        for (var offset = 0; ; )
        {
            var response = (await client.CallAsync(X.Msg("FindFolder", new XAttribute("Traversal", "Deep"),
                X.Msg("FolderShape", X.Typ("BaseShape", "IdOnly"), X.Typ("AdditionalProperties",
                    X.Field("folder:DisplayName"), X.Field("folder:ParentFolderId"), X.Field("folder:FolderClass"),
                    X.Field("folder:TotalCount"), X.Field("folder:UnreadCount"), extraFields.Select(X.Field), X.Extended(HiddenTag, "Boolean"))),
                X.Msg("IndexedPageFolderView", new XAttribute("MaxEntriesReturned", 500), new XAttribute("Offset", offset), new XAttribute("BasePoint", "Beginning")),
                X.Msg("ParentFolderIds", X.Typ("FolderId", new XAttribute("Id", rootId)))), cancellationToken)).Single();
            var page = response.Element(X.M + "RootFolder");
            var found = page?.Element(X.T + "Folders")?.Elements().ToList() ?? [];
            folders.AddRange(found.Where(f => f.Name.LocalName != "SearchFolder" && f.ExtendedValue(HiddenTag) is not "true"));
            if (found.Count == 0 || page?.Attribute("IncludesLastItemInRange")?.Value != "false")
            {
                break;
            }

            offset = int.TryParse(page.Attribute("IndexedPagingOffset")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var next) ? next : offset + found.Count;
        }

        return (rootId, folders);
    }

    public static string FolderIdOf(XElement folder) => folder.Element(X.T + "FolderId")!.Attribute("Id")!.Value;

    public static string? ParentIdOf(XElement folder) => folder.Element(X.T + "ParentFolderId")?.Attribute("Id")?.Value;

    /// <summary>The ids of well-known folders (those the mailbox has).</summary>
    public static async Task<Dictionary<string, string>> WellKnownAsync(EwsClient client, IReadOnlyList<string> names, CancellationToken cancellationToken)
    {
        var responses = await client.CallAsync(X.Msg("GetFolder",
            X.Msg("FolderShape", X.Typ("BaseShape", "IdOnly")),
            X.Msg("FolderIds", names.Select(X.FolderId))), cancellationToken, "ErrorFolderNotFound");
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, response) in names.Zip(responses))
        {
            if (response.IsSuccess() && response.Descendants(X.T + "FolderId").FirstOrDefault()?.Attribute("Id")?.Value is { } id)
            {
                result[name] = id;
            }
        }

        return result;
    }

    /// <summary>
    /// Item ids (with change keys) of a folder: all, or the newest <paramref name="max"/> after skipping
    /// <paramref name="offset"/> (by received date). Also whether there are more. <paramref name="kind"/>: only items of
    /// this type (e.g. "Contact", not "DistributionList").
    /// </summary>
    public static async Task<(IReadOnlyList<(string Id, string? ChangeKey)> Items, bool More)> FindItemIdsAsync(
        EwsClient client, string folderId, CancellationToken cancellationToken, int max = int.MaxValue, int offset = 0, bool newestFirst = false, string? query = null,
        string? kind = null)
    {
        var items = new List<(string, string?)>();
        var more = false;
        while (items.Count < max)
        {
            var size = Math.Min(500, max - items.Count);
            var response = (await client.CallAsync(X.Msg("FindItem", new XAttribute("Traversal", "Shallow"),
                X.Msg("ItemShape", X.Typ("BaseShape", "IdOnly")),
                X.Msg("IndexedPageItemView", new XAttribute("MaxEntriesReturned", size), new XAttribute("Offset", offset), new XAttribute("BasePoint", "Beginning")),
                newestFirst ? X.Msg("SortOrder", X.Typ("FieldOrder", new XAttribute("Order", "Descending"), X.Field("item:DateTimeReceived"))) : null,
                X.Msg("ParentFolderIds", X.FolderId(folderId)),
                query is null ? null : X.Msg("QueryString", query)), cancellationToken)).Single();
            var page = response.Element(X.M + "RootFolder");
            var found = page?.Element(X.T + "Items")?.Elements().ToList() ?? [];
            items.AddRange(found.Where(i => i.Id() is not null && (kind is null || i.Name.LocalName == kind)).Select(i => (i.Id()!, i.ChangeKey())));
            more = page?.Attribute("IncludesLastItemInRange")?.Value == "false";
            if (found.Count == 0 || !more)
            {
                break;
            }

            offset += found.Count;
        }

        return (items, more);
    }

    /// <summary>Loads items with the given properties, in groups; items gone meanwhile are left out.</summary>
    public static async Task<IReadOnlyList<XElement>> GetItemsAsync(EwsClient client, IEnumerable<string> ids, XElement shape, CancellationToken cancellationToken, int batch = 100)
    {
        var responses = await client.CallInBatchesAsync(ids, batch, chunk => X.Msg("GetItem", shape, X.Msg("ItemIds", chunk.Select(id => X.ItemId(id)))),
            cancellationToken, "ErrorItemNotFound");
        return [.. responses.Where(r => r.IsSuccess()).Select(r => r.FirstItem()).OfType<XElement>()];
    }

    /// <summary>An item shape: ids plus <paramref name="fields"/> (field URIs or extended field elements).</summary>
    public static XElement Shape(IEnumerable<object> fields, string? bodyType = null, bool mime = false) =>
        X.Msg("ItemShape", X.Typ("BaseShape", "IdOnly"),
            mime ? X.Typ("IncludeMimeContent", "true") : null,
            bodyType is null ? null : X.Typ("BodyType", bodyType),
            X.Typ("AdditionalProperties", fields.Select(f => f is string uri ? X.Field(uri) : f)));
}
