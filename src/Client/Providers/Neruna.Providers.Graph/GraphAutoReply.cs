using System.Globalization;
using System.Net;
using System.Text.Json;
using Neruna.Core.Mail;
using static Neruna.Core.Localization.Texts;

namespace Neruna.Providers.Graph;

/// <summary>
/// The out-of-office reply of a Microsoft 365 / Outlook.com mailbox (mailboxSettings/automaticRepliesSetting): on, off
/// or in a period; the same text for colleagues and everyone else. Needs MailboxSettings.ReadWrite – accounts signed in
/// before that permission came sign in once more.
/// </summary>
internal static class GraphAutoReply
{
    private const string Path = "me/mailboxSettings";

    public const AutoReplyFeatures Features = AutoReplyFeatures.Schedule;

    public static async Task<(AutoReply Reply, AutoReplyFeatures Features)> GetAsync(GraphClient graph, CancellationToken cancellationToken)
    {
        await EnsureGrantedAsync(graph, cancellationToken);
        var setting = await graph.GetAsync(Path + "/automaticRepliesSetting", cancellationToken);
        var status = setting.Str("status") ?? "disabled";
        var message = ToText(setting.Str("internalReplyMessage") ?? setting.Str("externalReplyMessage") ?? string.Empty);
        var scheduled = status == "scheduled";
        var reply = new AutoReply(
            status != "disabled",
            message,
            scheduled ? DateOf(setting, "scheduledStartDateTime") : null,
            scheduled ? DateOf(setting, "scheduledEndDateTime") : null);
        return (reply, Features);
    }

    public static async Task SetAsync(GraphClient graph, AutoReply reply, CancellationToken cancellationToken)
    {
        await EnsureGrantedAsync(graph, cancellationToken);
        var html = ToHtml(reply.Message);
        var setting = new Dictionary<string, object?>
        {
            // Off keeps the text, so it is there again next time.
            ["status"] = !reply.IsEnabled ? "disabled" : reply.IsScheduled ? "scheduled" : "alwaysEnabled",
            ["externalAudience"] = "all",
            ["internalReplyMessage"] = html,
            ["externalReplyMessage"] = html,
        };
        if (reply.IsScheduled)
        {
            setting["scheduledStartDateTime"] = Utc(reply.Start!.Value);
            setting["scheduledEndDateTime"] = Utc(reply.End!.Value);
        }

        await graph.SendJsonAsync(HttpMethod.Patch, Path, new Dictionary<string, object> { ["automaticRepliesSetting"] = setting }, cancellationToken);
    }

    private static async Task EnsureGrantedAsync(GraphClient graph, CancellationToken cancellationToken)
    {
        if (!await graph.GrantsAsync(MicrosoftAccount.MailboxSettingsScope, cancellationToken))
        {
            throw new AutoReplyUnavailableException(T("Für die Abwesenheitsnotiz braucht Neruna eine zusätzliche Berechtigung. Bitte einmal neu bei Microsoft anmelden: Einstellungen → Konten → Bearbeiten → «Erneut bei Microsoft anmelden»."));
        }
    }

    private static Dictionary<string, string> Utc(DateTimeOffset value) => new()
    {
        ["dateTime"] = value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture),
        ["timeZone"] = "UTC",
    };

    // Graph answers in UTC unless the mailbox asks for another zone; anything else is read as UTC as well.
    private static DateTimeOffset? DateOf(JsonElement setting, string name) =>
        setting.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Object
        && DateTime.TryParse(value.Str("dateTime"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var utc)
            ? new DateTimeOffset(utc, TimeSpan.Zero)
            : null;

    internal static string ToHtml(string text) =>
        "<html><body><div>" + WebUtility.HtmlEncode(text.Replace("\r\n", "\n", StringComparison.Ordinal)).Replace("\n", "<br>", StringComparison.Ordinal) + "</div></body></html>";

    internal static string ToText(string html) =>
        html.Length == 0 ? string.Empty : HtmlText.ToPlainText(html).Trim();
}
