namespace Neruna.Desktop.Infrastructure;

/// <summary>What the system hands to Neruna: mailto: links and .eml files (other arguments are options).</summary>
internal static class SystemOpen
{
    /// <summary>A later start without anything to open: just bring the window to the front.</summary>
    public const string Activate = "--activate";

    public static bool IsMailto(string item) => Neruna.Core.Mail.MailtoLink.IsMailto(item);

    public static bool IsEml(string item) => item.EndsWith(".eml", StringComparison.OrdinalIgnoreCase) && File.Exists(item);

    public static bool IsOpenable(string item) => item == Activate || IsMailto(item) || IsEml(item);
}
