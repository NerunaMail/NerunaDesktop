using MimeKit;

namespace Neruna.Core.Mail;

/// <summary>
/// "Übermittlungsbestätigung" and "Lesebestätigung" for a mail being sent. The read receipt is the standard header
/// (RFC 8098) every program understands; the delivery receipt is no header but a request to the server – SMTP DSN
/// (RFC 3461), the item's flag at Exchange and Microsoft 365. So it travels to the provider as an internal header,
/// which the provider takes off before the mail goes out.
/// </summary>
public static class Receipts
{
    private const string DeliveryHeader = "X-Neruna-Delivery-Receipt";

    public static void Request(MimeMessage message, bool delivery, bool read)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (delivery)
        {
            message.Headers[DeliveryHeader] = "yes";
        }

        if (read && message.From.Mailboxes.FirstOrDefault() is { } from)
        {
            message.Headers[HeaderId.DispositionNotificationTo] = new MailboxAddress(from.Name, from.Address).ToString(true);
        }
    }

    /// <summary>Whether a delivery receipt is asked for; the internal header is removed (for the provider sending it).</summary>
    public static bool TakeDeliveryRequest(MimeMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return message.Headers.Remove(DeliveryHeader);
    }

    public static bool WantsReadReceipt(MimeMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return message.Headers.Contains(HeaderId.DispositionNotificationTo);
    }
}
