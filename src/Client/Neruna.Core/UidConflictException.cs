using static Neruna.Core.Localization.Texts;
namespace Neruna.Core;

/// <summary>
/// The server refused a new event or contact because one with the same UID is already in the collection – also one the
/// user deleted and the server still keeps (Nextcloud's trash bin), which the app no longer sees (CalDAV no-uid-conflict).
/// </summary>
public sealed class UidConflictException : Exception
{
    public UidConflictException()
        : base(T("Ein Eintrag mit derselben Kennung ist schon auf dem Server (evtl. im Papierkorb)."))
    {
    }

    public UidConflictException(string message)
        : base(message)
    {
    }

    public UidConflictException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
