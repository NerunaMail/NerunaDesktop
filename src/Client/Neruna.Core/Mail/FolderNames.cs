using static Neruna.Core.Localization.Texts;

namespace Neruna.Core.Mail;

/// <summary>Checks a name for a new or renamed folder before it goes to the server.</summary>
public static class FolderNames
{
    /// <summary>What is wrong with <paramref name="name"/>, or null when it can be used.</summary>
    /// <param name="forbidden">Characters the server does not take (<see cref="IMailProvider.ForbiddenFolderNameCharacters"/>).</param>
    /// <param name="siblings">The names of the folders beside it (the same name twice is not possible).</param>
    public static string? Problem(string? name, string forbidden, IEnumerable<string> siblings)
    {
        ArgumentNullException.ThrowIfNull(forbidden);
        ArgumentNullException.ThrowIfNull(siblings);
        if (string.IsNullOrWhiteSpace(name))
        {
            return T("Bitte einen Namen eingeben.");
        }

        if (name.FirstOrDefault(c => forbidden.Contains(c, StringComparison.Ordinal)) is var bad and not '\0')
        {
            return F("Das Zeichen «{0}» ist in Ordnernamen nicht erlaubt.", bad);
        }

        if (name.Any(char.IsControl))
        {
            return T("Der Name enthält unsichtbare Steuerzeichen.");
        }

        if (name != name.Trim())
        {
            return T("Der Name darf nicht mit einem Leerzeichen beginnen oder enden.");
        }

        if (name.Length > 200)
        {
            return T("Der Name ist zu lang.");
        }

        return siblings.Any(s => string.Equals(s, name, StringComparison.OrdinalIgnoreCase))
            ? F("Einen Ordner «{0}» gibt es hier schon.", name)
            : null;
    }
}
