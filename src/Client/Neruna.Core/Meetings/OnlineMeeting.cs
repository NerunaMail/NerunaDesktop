using System.Text;
using Ical.Net;
using Ical.Net.CalendarComponents;
using Ical.Net.DataTypes;
using static Neruna.Core.Localization.Texts;

namespace Neruna.Core.Meetings;

/// <summary>The services Neruna knows by name (stored in <see cref="OnlineMeeting.Service"/> and <see cref="MeetingService.Kind"/>).</summary>
public static class MeetingKinds
{
    public const string Teams = "teams";
    public const string Zoom = "zoom";
    public const string Jitsi = "jitsi";
    public const string Google = "google";
    public const string Other = "other";

    /// <summary>"Microsoft Teams", "Zoom" … for lists and the join text.</summary>
    public static string NameOf(string kind) => kind switch
    {
        Teams => "Microsoft Teams",
        Zoom => "Zoom",
        Jitsi => "Jitsi Meet",
        Google => "Google Meet",
        _ => T("Online-Besprechung"),
    };

    /// <summary>The service a join link belongs to, from its host (for events made elsewhere).</summary>
    public static string FromUrl(string url)
    {
        var host = Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host.ToLowerInvariant() : string.Empty;
        return host switch
        {
            _ when host.EndsWith("teams.microsoft.com", StringComparison.Ordinal) || host.EndsWith("teams.live.com", StringComparison.Ordinal) => Teams,
            _ when host == "zoom.us" || host.EndsWith(".zoom.us", StringComparison.Ordinal) => Zoom,
            "meet.google.com" => Google,
            _ when host.Contains("jitsi", StringComparison.Ordinal) || host == "meet.jit.si" => Jitsi,
            _ => Other,
        };
    }
}

/// <summary>
/// The online meeting of an event: which service and the link to join. Stored in the event as RFC 7986
/// <c>CONFERENCE</c> (read by many programs) plus the join text in the description, so every recipient gets the link,
/// whatever program they use. Teams links also go into <c>X-MICROSOFT-SKYPETEAMSMEETINGURL</c>, which Microsoft's
/// programs show as a "Join" button.
/// </summary>
/// <param name="Service">One of <see cref="MeetingKinds"/>.</param>
/// <param name="JoinUrl">Null: the calendar's server creates the meeting when the event is saved (Teams through Exchange).</param>
/// <param name="Details">Meeting ID, passcode, dial-in – lines for the description.</param>
public sealed record OnlineMeeting(string Service, string? JoinUrl, string? Details = null)
{
    public const string ConferenceProperty = "CONFERENCE";
    public const string TeamsUrlProperty = "X-MICROSOFT-SKYPETEAMSMEETINGURL";
    public const string GoogleUrlProperty = "X-GOOGLE-CONFERENCE";

    /// <summary>The service, kept so a link Neruna did not make is still labelled (and a pending Teams meeting is known).</summary>
    public const string ServiceProperty = "X-NERUNA-MEETING";

    // Lines around the join text: it can be found again (removed or replaced) and stands out in every program.
    private const string Rule = "________________________________________";

    public string Name => MeetingKinds.NameOf(Service);

    /// <summary>Whether the server still has to create it (on saving).</summary>
    public bool IsPending => JoinUrl is null;

    /// <summary>The meeting of an event; null if it has none.</summary>
    public static OnlineMeeting? Of(CalendarEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);
        var service = Text(evt, ServiceProperty);
        var url = evt.Properties.Where(p => p.Name == ConferenceProperty).Select(p => p.Value?.ToString()).FirstOrDefault(IsLink)
                  ?? new[] { Text(evt, TeamsUrlProperty), Text(evt, GoogleUrlProperty) }.FirstOrDefault(IsLink);
        if (url is null)
        {
            return service is { Length: > 0 } ? new OnlineMeeting(service, null) : null;
        }

        return new OnlineMeeting(service is { Length: > 0 } && service != MeetingKinds.Other ? service : MeetingKinds.FromUrl(url), url);
    }

    /// <summary>Writes <paramref name="meeting"/> into the event (or removes what Neruna wrote, for null).</summary>
    public static void Apply(CalendarEvent evt, OnlineMeeting? meeting)
    {
        ArgumentNullException.ThrowIfNull(evt);
        if (Of(evt) == meeting)
        {
            return;
        }

        foreach (var property in evt.Properties.Where(p => p.Name is ConferenceProperty or TeamsUrlProperty or ServiceProperty).ToList())
        {
            evt.Properties.Remove(property);
        }

        if (meeting is null)
        {
            return;
        }

        evt.Properties.Add(new CalendarProperty(ServiceProperty, meeting.Service));
        if (meeting.JoinUrl is not { } url)
        {
            return;
        }

        var conference = new CalendarProperty(ConferenceProperty, url);
        conference.Parameters.Add("VALUE", "URI");
        conference.Parameters.Add("FEATURE", "VIDEO");
        conference.Parameters.Add("LABEL", meeting.Name);
        evt.Properties.Add(conference);
        if (meeting.Service == MeetingKinds.Teams)
        {
            evt.Properties.Add(new CalendarProperty(TeamsUrlProperty, url));
        }
    }

    /// <summary>The join text for the description: service, link, details – between two lines.</summary>
    public string JoinText()
    {
        var text = new StringBuilder();
        text.Append(Rule).Append('\n')
            .Append(F("{0}-Besprechung", Name)).Append('\n')
            .Append(T("Teilnehmen:")).Append(' ').Append(JoinUrl).Append('\n');
        if (!string.IsNullOrWhiteSpace(Details))
        {
            text.Append(Details.Trim()).Append('\n');
        }

        return text.Append(Rule).ToString();
    }

    /// <summary>The description with this meeting's join text at the end (an earlier one from Neruna replaced).</summary>
    public string AddTo(string? description)
    {
        var rest = RemoveFrom(description, JoinUrl).TrimEnd();
        return rest.Length == 0 ? JoinText() : rest + "\n\n" + JoinText();
    }

    /// <summary>The description without the join text of <paramref name="joinUrl"/> (and its lines).</summary>
    public static string RemoveFrom(string? description, string? joinUrl)
    {
        if (string.IsNullOrEmpty(description))
        {
            return string.Empty;
        }

        var text = description.Replace("\r\n", "\n", StringComparison.Ordinal);
        var start = 0;
        while ((start = text.IndexOf(Rule, start, StringComparison.Ordinal)) >= 0)
        {
            var end = text.IndexOf(Rule, start + Rule.Length, StringComparison.Ordinal);
            if (end < 0)
            {
                break;
            }

            var block = text[start..(end + Rule.Length)];
            if (joinUrl is null || block.Contains(joinUrl, StringComparison.Ordinal))
            {
                return (text[..start].TrimEnd() + "\n\n" + text[(end + Rule.Length)..].TrimStart()).Trim();
            }

            start = end + Rule.Length;
        }

        return description;
    }

    private static string? Text(CalendarEvent evt, string name) => evt.Properties.Get<string>(name)?.Trim();

    private static bool IsLink(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);
}
