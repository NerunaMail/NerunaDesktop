namespace Neruna.Core.Security;

/// <summary>What a link would start: the browser, a new mail, the phone, a file, or another program.</summary>
public enum LinkKind
{
    Web,
    Mail,
    Phone,
    File,
    Program,
}

/// <summary>
/// Which links Neruna opens how. Web, mail and phone links open right away; a link into another program
/// ("zoommtg://", "ftp://", "obsidian://" …) only after asking, which can be switched off per kind; file links always
/// ask. Script, data and known abused system links are never opened.
/// </summary>
public static class LinkKinds
{
    // Run code or system tools without a real target (e.g. "ms-msdt:", "search-ms:" were used for attacks).
    private static readonly HashSet<string> Blocked = new(StringComparer.OrdinalIgnoreCase)
    {
        "javascript", "vbscript", "data", "about", "blob", "jar", "view-source", "res", "mk", "its", "ms-its", "hcp", "shell",
        "ms-msdt", "search", "search-ms", "ms-search", "ms-officecmd", "ms-cxh", "ms-cxh-full", "ms-appinstaller", "ms-settings",
    };

    public static LinkKind Of(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        return uri.Scheme.ToLowerInvariant() switch
        {
            "http" or "https" => LinkKind.Web,
            "mailto" => LinkKind.Mail,
            "tel" => LinkKind.Phone,
            "file" => LinkKind.File,
            _ => LinkKind.Program,
        };
    }

    public static bool IsBlocked(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        return Blocked.Contains(uri.Scheme);
    }

    /// <summary>"Nicht mehr fragen" is offered for program links only; a file link may start anything on the disk.</summary>
    public static bool CanTrust(Uri uri) => Of(uri) == LinkKind.Program && !IsBlocked(uri);
}
