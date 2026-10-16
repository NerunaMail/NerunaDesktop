using System.Net;
using System.Text.RegularExpressions;
using MimeKit;
using Neruna.Core.Contacts;

namespace Neruna.Core.Mail;

/// <summary>Who a text template is filled for: the first recipient in "An", completed from the address book.</summary>
public sealed record TemplateRecipient(string Email, string? FullName, string? FirstName, string? LastName, string? Company)
{
    public static TemplateRecipient None { get; } = new(string.Empty, null, null, null, null);
}

/// <summary>
/// Placeholders in text templates, filled when the template is inserted while writing: «Sehr geehrte Frau
/// {{recipient_last_name}}». A known field without a value becomes empty; anything else in braces stays as typed, so a
/// misspelt one shows. Templates of the organisation reach the app with these placeholders unchanged (the portal fills
/// only the sender's and the organisation's data).
/// </summary>
public static partial class TemplateFields
{
    /// <summary>The fields, in the order the help shows them.</summary>
    public static IReadOnlyList<string> Names { get; } =
        ["recipient_full_name", "recipient_first_name", "recipient_last_name", "recipient_email", "recipient_company"];

    public static bool HasFields(string? html) => html is not null && Field().Matches(html).Any(m => Names.Contains(m.Groups[1].Value.ToLowerInvariant()));

    /// <summary>The template with the recipient's data in (HTML-encoded); <paramref name="recipient"/> null: all empty.</summary>
    public static string Fill(string html, TemplateRecipient? recipient)
    {
        ArgumentNullException.ThrowIfNull(html);
        var r = recipient ?? TemplateRecipient.None;
        return Field().Replace(html, match => match.Groups[1].Value.ToLowerInvariant() switch
        {
            "recipient_full_name" => Encode(r.FullName),
            "recipient_first_name" => Encode(r.FirstName),
            "recipient_last_name" => Encode(r.LastName),
            "recipient_email" => Encode(r.Email),
            "recipient_company" => Encode(r.Company),
            _ => match.Value,
        });
    }

    /// <summary>
    /// The first address in the "An" field, with name and company from the contact that has this address; without
    /// one, first and last name come from the name in the field ("Anna Muster" or "Muster, Anna").
    /// </summary>
    public static async Task<TemplateRecipient?> ResolveAsync(string? to, Func<string, Task<ContactCard?>> findContact)
    {
        ArgumentNullException.ThrowIfNull(findContact);
        if (string.IsNullOrWhiteSpace(to))
        {
            return null;
        }

        // The whole field first ("Muster, Anna" <…> has a comma in the name); while it is half typed, part by part.
        var first = InternetAddressList.TryParse(to, out var list)
            ? list.Mailboxes.FirstOrDefault(m => m.Address.Contains('@', StringComparison.Ordinal))
            : null;
        foreach (var part in first is null ? to.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) : [])
        {
            if (MailboxAddress.TryParse(part, out var mailbox) && mailbox.Address.Contains('@', StringComparison.Ordinal))
            {
                first = mailbox;
                break;
            }
        }

        if (first is null)
        {
            return null;
        }

        var email = first.Address;
        if (await findContact(email) is { } card)
        {
            var full = Blank(card.DisplayName) ?? Blank($"{card.GivenName} {card.FamilyName}".Trim()) ?? Blank(first.Name);
            var (given, family) = SplitName(first.Name);
            return new TemplateRecipient(email, full, Blank(card.GivenName) ?? given, Blank(card.FamilyName) ?? family, Blank(card.Organization));
        }

        var (firstName, lastName) = SplitName(first.Name);
        return new TemplateRecipient(email, Blank(first.Name), firstName, lastName, null);
    }

    /// <summary>"Anna Muster" → (Anna, Muster); "Muster, Anna" → (Anna, Muster); "Anna Maria Muster" → (Anna Maria, Muster); one word: unknown.</summary>
    public static (string? First, string? Last) SplitName(string? name)
    {
        var value = Blank(name?.Trim('"', '\'', ' '));
        if (value is null || value.Contains('@', StringComparison.Ordinal))
        {
            return (null, null);
        }

        var comma = value.IndexOf(',', StringComparison.Ordinal);
        if (comma > 0)
        {
            return (Blank(value[(comma + 1)..].Trim()), Blank(value[..comma].Trim()));
        }

        var space = value.LastIndexOf(' ');
        return space > 0 ? (value[..space].Trim(), value[(space + 1)..].Trim()) : (null, null);
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string Encode(string? value) => WebUtility.HtmlEncode(value ?? string.Empty);

    [GeneratedRegex(@"\{\{\s*([A-Za-z_]+)\s*\}\}")]
    private static partial Regex Field();
}
