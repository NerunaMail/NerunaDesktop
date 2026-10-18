using System.Globalization;
using System.Net;
using System.Text;
using Neruna.Core.Auth;
using Neruna.Core.Meetings;
using Neruna.Core.Security;
using static Neruna.Core.Localization.Texts;

namespace Neruna.Providers.Graph;

/// <summary>
/// Teams meetings with a sign-in of their own (Graph <c>me/onlineMeetings</c>): for calendars that are not in
/// Microsoft 365 – Exchange in your own network, CalDAV – or with another Microsoft account. The link goes into the
/// event like any other meeting's. Calendars of a Microsoft 365 account need none of this: Exchange creates Teams
/// meetings itself (<see cref="GraphCalendarProvider.GetOnlineMeetingKindsAsync"/>).
/// </summary>
public sealed class TeamsMeetingProvider(HttpClient http, ICredentialStore credentials) : IMeetingProvider
{
    public const string UserKey = "teams.user";

    private readonly OAuthClient _oauth = new(http);

    public string Kind => MeetingKinds.Teams;

    public bool IsAvailable => MicrosoftAccount.IsConfigured;

    public async Task<MeetingService> ConnectAsync(Guid id, string? server, IBrowserLauncher browser, CancellationToken cancellationToken = default)
    {
        try
        {
            var tokens = await _oauth.SignInAsync(MicrosoftAccount.TeamsEndpoints, null, browser, cancellationToken);
            await OAuthTokenSource.StoreAsync(credentials, id, tokens, cancellationToken);
            var me = await ClientFor(id).GetAsync("me?$select=mail,userPrincipalName", cancellationToken);
            var user = me.Str("mail") ?? me.Str("userPrincipalName") ?? string.Empty;
            return new MeetingService(id, Kind, $"{MeetingKinds.NameOf(Kind)} ({user})", new Dictionary<string, string> { [UserKey] = user });
        }
        catch (Exception ex) when (ex is OAuthException or GraphException or HttpRequestException)
        {
            await credentials.DeleteSecretAsync(id, CancellationToken.None);
            throw new MeetingException(ex.Message, inner: ex);
        }
    }

    public async Task<OnlineMeeting> CreateAsync(MeetingService service, MeetingRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            var meeting = await ClientFor(service.Id).SendJsonAsync(HttpMethod.Post, "me/onlineMeetings", new
            {
                subject = request.Subject,
                startDateTime = request.Start.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
                endDateTime = request.End.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
            }, cancellationToken);
            var url = meeting.Str("joinWebUrl") ?? meeting.Str("joinUrl")
                      ?? throw new MeetingException(T("Teams hat keinen Link zur Besprechung geliefert."));
            return new OnlineMeeting(Kind, url, DetailsOf(meeting));
        }
        catch (OAuthException ex)
        {
            throw new MeetingException(ex.Message, ex.SignInRequired, ex);
        }
        catch (GraphException ex) when (ex.Status is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
        {
            throw new MeetingException(T("Teams lässt dieses Konto keine Besprechungen erstellen (nur Geschäfts- oder Schulkonten mit Teams-Lizenz). ") + ex.Message, inner: ex);
        }
        catch (GraphException ex)
        {
            throw new MeetingException(T("Die Teams-Besprechung konnte nicht erstellt werden: ") + ex.Message, inner: ex);
        }
    }

    public Task DisconnectAsync(MeetingService service, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(service);
        return credentials.DeleteSecretAsync(service.Id, cancellationToken);
    }

    private GraphClient ClientFor(Guid id) => new(http, new OAuthTokenSource(_oauth, MicrosoftAccount.TeamsEndpoints, credentials, id));

    // Meeting ID and passcode (to join from the Teams app by number), dial-in where the organisation has it.
    private static string? DetailsOf(System.Text.Json.JsonElement meeting)
    {
        var details = new StringBuilder();
        if (meeting.Obj("joinMeetingIdSettings").Str("joinMeetingId") is { Length: > 0 } id)
        {
            details.Append(T("Besprechungs-ID:")).Append(' ').Append(id).Append('\n');
            if (meeting.Obj("joinMeetingIdSettings").Str("passcode") is { Length: > 0 } passcode)
            {
                details.Append(T("Kenncode:")).Append(' ').Append(passcode).Append('\n');
            }
        }

        var audio = meeting.Obj("audioConferencing");
        if (audio.Str("tollNumber") is { Length: > 0 } number)
        {
            details.Append(T("Per Telefon:")).Append(' ').Append(number);
            if (audio.Str("conferenceId") is { Length: > 0 } conference)
            {
                details.Append(", ").Append(T("Konferenz-ID:")).Append(' ').Append(conference).Append('#');
            }

            details.Append('\n');
        }

        return details.Length == 0 ? null : details.ToString().TrimEnd();
    }
}
