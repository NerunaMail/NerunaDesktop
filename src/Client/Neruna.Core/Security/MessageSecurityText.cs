using static Neruna.Core.Localization.Texts;

namespace Neruna.Core.Security;

public enum MessageSecurityLevel
{
    None,
    Good,
    Warning,
    Bad,
}

/// <summary>The S/MIME status of an opened message in words – the same on the desktop and on the phone.</summary>
public static class MessageSecurityText
{
    public static (MessageSecurityLevel Level, string Text, string? Detail) Describe(MessageSecurityInfo security)
    {
        if (security.DecryptionError is { } error)
        {
            return (MessageSecurityLevel.Bad, T("Verschlüsselte Nachricht – kann nicht gelesen werden"), error);
        }

        var encrypted = security.WasEncrypted ? T(" · Verschlüsselt") : string.Empty;
        if (security.Signatures.FirstOrDefault() is not { } signature)
        {
            return security.WasEncrypted ? (MessageSecurityLevel.Good, T("Verschlüsselt (S/MIME)"), null) : (MessageSecurityLevel.None, string.Empty, null);
        }

        var signer = signature.SignerEmails.Count > 0 ? $"{signature.SignerName} <{signature.SignerEmails[0]}>" : signature.SignerName;
        return signature.Status switch
        {
            SignatureStatus.Valid when signature.EmailMatchesSender =>
                (MessageSecurityLevel.Good, F("Digital signiert von {0} · Signatur gültig{1}", signer, encrypted), F("Ausgestellt von {0}", signature.IssuerName)),
            SignatureStatus.Valid =>
                (MessageSecurityLevel.Warning, F("Signatur gültig, aber das Zertifikat ({0}) gehört nicht zur Absenderadresse{1}", signer, encrypted), null),
            SignatureStatus.ValidUntrusted =>
                (MessageSecurityLevel.Warning, F("Signiert von {0} · Aussteller «{1}» ist nicht vertrauenswürdig{2}", signer, signature.IssuerName, encrypted),
                    T("Die Nachricht ist unverändert. Um dem Aussteller zu vertrauen, dessen Stammzertifikat unter Einstellungen → Zertifikate importieren.")),
            _ => (MessageSecurityLevel.Bad, T("Ungültige Signatur – die Nachricht wurde nach dem Signieren verändert oder die Signatur ist beschädigt"), signature.Detail),
        };
    }
}
