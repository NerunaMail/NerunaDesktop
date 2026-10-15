using System.Globalization;
using System.Net;
using System.Xml.Linq;
using Neruna.Core.Mail;
using static Neruna.Core.Localization.Texts;

namespace Neruna.Providers.Ews;

/// <summary>
/// The out-of-office reply through EWS (GetUserOofSettings/SetUserOofSettings): Exchange answers inside and outside the
/// organisation with the same text; a period is kept by Exchange itself ("Scheduled").
/// </summary>
internal sealed partial class EwsMailProvider : IAutoReplyProvider
{
    private string MailboxAddress => EwsSettings.FromDictionary(connection.Settings).Mailbox;

    public async Task<(AutoReply Reply, AutoReplyFeatures Features)> GetAutoReplyAsync(CancellationToken cancellationToken = default)
    {
        var client = await ClientAsync(cancellationToken);
        XElement response;
        try
        {
            response = await client.CallSingleAsync(X.Msg("GetUserOofSettingsRequest", X.Typ("Mailbox", X.Typ("Address", MailboxAddress))), cancellationToken);
        }
        catch (EwsException ex)
        {
            throw new AutoReplyUnavailableException(F("Exchange gibt die Abwesenheitsnotiz nicht heraus: {0}", ex.Message), ex);
        }

        var settings = response.Descendants(X.T + "OofSettings").FirstOrDefault();
        var state = settings.Val("OofState");
        var html = settings?.Element(X.T + "InternalReply").Val("Message") ?? settings?.Element(X.T + "ExternalReply").Val("Message") ?? string.Empty;
        var duration = settings?.Element(X.T + "Duration");
        var scheduled = state == "Scheduled";
        var reply = new AutoReply(
            state is "Enabled" or "Scheduled",
            HtmlOrText(html),
            scheduled ? Time(duration.Val("StartTime")) : null,
            scheduled ? Time(duration.Val("EndTime")) : null);
        return (reply, AutoReplyFeatures.Schedule);
    }

    public async Task SetAutoReplyAsync(AutoReply reply, IReadOnlyList<string> ownAddresses, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reply);
        var client = await ClientAsync(cancellationToken);
        var state = !reply.IsEnabled ? "Disabled" : reply.IsScheduled ? "Scheduled" : "Enabled";
        var message = Html(reply.Message);
        var request = X.Msg("SetUserOofSettingsRequest",
            X.Typ("Mailbox", X.Typ("Address", MailboxAddress)),
            X.Typ("UserOofSettings",
                X.Typ("OofState", state),
                X.Typ("ExternalAudience", "All"),
                reply.IsScheduled ? X.Typ("Duration", X.Typ("StartTime", X.Utc(reply.Start!.Value)), X.Typ("EndTime", X.Utc(reply.End!.Value))) : null,
                X.Typ("InternalReply", X.Typ("Message", message)),
                X.Typ("ExternalReply", X.Typ("Message", message))));
        try
        {
            await client.CallSingleAsync(request, cancellationToken);
        }
        catch (EwsException ex)
        {
            throw new AutoReplyUnavailableException(F("Exchange hat die Abwesenheitsnotiz abgelehnt: {0}", ex.Message), ex);
        }
    }

    // Exchange keeps the reply as HTML: the plain text with its line breaks, and back.
    private static string Html(string text) =>
        "<html><body>" + string.Join("<br>", WebUtility.HtmlEncode(text.Replace("\r\n", "\n", StringComparison.Ordinal)).Split('\n')) + "</body></html>";

    private static string HtmlOrText(string value) =>
        value.Contains('<', StringComparison.Ordinal) ? HtmlText.ToPlainText(value).Trim() : value.Trim();

    private static DateTimeOffset? Time(string? value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var time) ? time.ToLocalTime() : null;
}
