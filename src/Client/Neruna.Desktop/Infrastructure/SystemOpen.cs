namespace Neruna.Desktop.Infrastructure;

/// <summary>What the system hands to Neruna: mailto: links and .eml files (other arguments are options).</summary>
internal static class SystemOpen
{
    /// <summary>A later start without anything to open: just bring the window to the front.</summary>
    public const string Activate = "--activate";

    /// <summary>Handed to the other open profiles before an update is installed: close (cleanly) now.</summary>
    public const string CloseForUpdate = "--close-for-update";

    /// <summary>After an update: the profiles that were open besides this one (ids, comma-separated) open again.</summary>
    public const string Reopen = "--reopen";

    /// <summary>The ids after <see cref="Reopen"/>.</summary>
    public static IReadOnlyList<Guid> ReopenIn(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var index = args.ToList().IndexOf(Reopen);
        return index < 0 || index + 1 >= args.Count
            ? []
            : [.. args[index + 1].Split(',').Select(t => Guid.TryParse(t, out var id) ? id : Guid.Empty).Where(id => id != Guid.Empty)];
    }

    public static bool IsMailto(string item) => Neruna.Core.Mail.MailtoLink.IsMailto(item);

    public static bool IsEml(string item) => item.EndsWith(".eml", StringComparison.OrdinalIgnoreCase) && File.Exists(item);

    public static bool IsOpenable(string item) => item == Activate || IsMailto(item) || IsEml(item);
}
