using System.Security.Cryptography;
using System.Text.Json;
using Neruna.Core.Security;
using PdfSharp.Pdf.IO;
using static Neruna.Core.Localization.Texts;

namespace Neruna.Core.Mail;

/// <summary>The PDF could not be protected (damaged, or a kind PDFsharp cannot rewrite).</summary>
public sealed class PdfProtectionException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Protects a PDF with a password to open it: AES-256 (PDF 2.0, "V5" – read by every current PDF reader), written with
/// PDFsharp (PDFium, which shows the pages, cannot encrypt). Everything is done on this device.
/// </summary>
public static class PdfProtection
{
    /// <summary>The PDF, readable only with <paramref name="password"/>; printing and copying stay allowed.</summary>
    /// <exception cref="PdfProtectionException">The PDF cannot be read or is protected already.</exception>
    public static byte[] Protect(byte[] pdf, string password)
    {
        ArgumentNullException.ThrowIfNull(pdf);
        ArgumentException.ThrowIfNullOrEmpty(password);
        try
        {
            using var input = new MemoryStream(pdf, writable: false);
            using var document = PdfReader.Open(input, PdfDocumentOpenMode.Modify);
            document.SecurityHandler.SetEncryptionToV5();
            document.SecuritySettings.UserPassword = password;

            // The owner password only guards the permissions (all allowed); a random one, so the open password
            // cannot be used to change them.
            document.SecuritySettings.OwnerPassword = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24));
            using var output = new MemoryStream();
            document.Save(output);
            return output.ToArray();
        }
        catch (Exception ex) when (ex is PdfReaderException or InvalidOperationException or NotSupportedException or IOException or ArgumentException)
        {
            throw new PdfProtectionException(T("Dieses PDF kann nicht mit einem Kennwort geschützt werden: ") + ex.Message, ex);
        }
    }

    /// <summary>A password that is easy to pass on by phone: four groups of four letters and digits (about 80 bits).</summary>
    public static string NewPassword()
    {
        const string alphabet = "abcdefghjkmnpqrstuvwxyzABCDEFGHJKMNPQRSTUVWXYZ23456789";
        var characters = Enumerable.Range(0, 16).Select(_ => alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)]).ToArray();
        return string.Join('-', characters.Chunk(4).Select(c => new string(c)));
    }
}

/// <summary>
/// Recipients that get PDFs only with a password (an address, or "@domain" for everyone there). The password is
/// agreed with them beforehand and kept in the system keychain under <see cref="Id"/>, never in the database.
/// </summary>
/// <param name="Automatic">Protect PDF attachments when sending, without asking.</param>
public sealed record PdfProtectionRule(Guid Id, string Recipient, bool Automatic)
{
    public bool Matches(string address)
    {
        ArgumentNullException.ThrowIfNull(address);
        var pattern = Recipient.Trim();
        return pattern.StartsWith('@')
            ? address.EndsWith(pattern, StringComparison.OrdinalIgnoreCase)
            : string.Equals(address.Trim(), pattern, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>Einstellungen → PDF → Sicherheit: the offer to protect PDFs, and the recipients with a password.</summary>
public sealed class PdfProtectionService(ISettingsStore settings, ICredentialStore credentials)
{
    /// <summary>Per device, like the passwords in the keychain.</summary>
    public const string RulesKey = "pdf.protection.rules";

    /// <summary>Whether unprotected PDF attachments offer "Mit Kennwort schützen" (on unless switched off).</summary>
    public const string OfferKey = "pdf.protection.offer";

    public async Task<bool> GetOfferAsync(CancellationToken cancellationToken = default) =>
        await settings.GetAsync(OfferKey, cancellationToken) != "false";

    public Task SetOfferAsync(bool offer, CancellationToken cancellationToken = default) =>
        settings.SetAsync(OfferKey, offer ? "true" : "false", cancellationToken);

    public async Task<IReadOnlyList<PdfProtectionRule>> GetRulesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await settings.GetAsync(RulesKey, cancellationToken) is { } json ? JsonSerializer.Deserialize<List<PdfProtectionRule>>(json) ?? [] : [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>Adds or changes a rule; <paramref name="password"/> null keeps the stored one.</summary>
    public async Task SaveRuleAsync(PdfProtectionRule rule, string? password, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rule);
        if (password is not null)
        {
            await credentials.SetSecretAsync(rule.Id, password, cancellationToken);
        }

        var rules = (await GetRulesAsync(cancellationToken)).Where(r => r.Id != rule.Id).Append(rule).ToList();
        await settings.SetAsync(RulesKey, JsonSerializer.Serialize(rules), cancellationToken);
    }

    public async Task RemoveRuleAsync(PdfProtectionRule rule, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rule);
        await credentials.DeleteSecretAsync(rule.Id, cancellationToken);
        await settings.SetAsync(RulesKey, JsonSerializer.Serialize((await GetRulesAsync(cancellationToken)).Where(r => r.Id != rule.Id).ToList()), cancellationToken);
    }

    public Task<string?> GetPasswordAsync(PdfProtectionRule rule, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rule);
        return credentials.GetSecretAsync(rule.Id, cancellationToken);
    }

    /// <summary>The rule for each recipient that has one (the address rule before a domain rule).</summary>
    public async Task<IReadOnlyList<(string Recipient, PdfProtectionRule Rule)>> MatchAsync(IEnumerable<string> recipients, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(recipients);
        var rules = await GetRulesAsync(cancellationToken);
        return [.. recipients.Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(r => (Recipient: r, Rule: rules.Where(rule => rule.Matches(r)).OrderBy(rule => rule.Recipient.StartsWith('@')).FirstOrDefault()))
            .Where(m => m.Rule is not null)
            .Select(m => (m.Recipient, m.Rule!))];
    }
}
