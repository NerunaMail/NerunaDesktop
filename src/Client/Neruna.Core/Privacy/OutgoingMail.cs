using MimeKit;
using MimeKit.Utils;

namespace Neruna.Core.Privacy;

/// <summary>
/// What a sent mail tells about the computer it was written on. MimeKit names the computer in Message-IDs and
/// Content-IDs ("…@DESKTOP-K3J2"); Neruna uses the sender's domain instead, and can leave out the time zone.
/// </summary>
public static class OutgoingMail
{
    // MimeKit's own choice: the computer's name (or "localhost.localdomain" without one).
    private static readonly Lazy<string> MachineDomain = new(() => MimeUtils.GenerateMessageId().Split('@')[^1]);

    /// <summary>A new Message-ID (or Content-ID) with the domain of <paramref name="senderAddress"/>.</summary>
    public static string NewMessageId(string? senderAddress) =>
        MimeUtils.GenerateMessageId(DomainNames.OfAddress(senderAddress) ?? "localhost.localdomain");

    /// <summary>Right before sending: a Message-ID that names the computer is replaced; the time in UTC if wanted.</summary>
    public static void Prepare(MimeMessage message, bool hideTimeZone)
    {
        ArgumentNullException.ThrowIfNull(message);
        var sender = message.From.Mailboxes.FirstOrDefault()?.Address;
        if (message.MessageId is null || NamesMachine(message.MessageId))
        {
            message.MessageId = NewMessageId(sender);
        }

        if (hideTimeZone)
        {
            // Through the header: MimeKit ignores a new Date that is the same moment in another zone.
            var utc = (message.Date == DateTimeOffset.MinValue ? DateTimeOffset.UtcNow : message.Date).ToUniversalTime();
            message.Headers[HeaderId.Date] = DateUtils.FormatDate(utc);
        }
    }

    private static bool NamesMachine(string id) =>
        MachineDomain.Value != "localhost.localdomain" && id.EndsWith("@" + MachineDomain.Value, StringComparison.OrdinalIgnoreCase);
}
