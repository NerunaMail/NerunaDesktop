using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Neruna.Core.Auth;
using Neruna.Core.Meetings;
using Neruna.Core.Security;
using static Neruna.Core.Localization.Texts;

namespace Neruna.Providers.Zoom;

/// <summary>
/// Neruna's app in the Zoom App Marketplace (General app, user-managed, "public client" with PKCE – no secret). The
/// client id is not secret; while it is empty, Zoom stays switched off in the settings. Redirect: the app's "PKCE OAuth
/// Redirect URL" is <c>http://127.0.0.1/callback</c>, any port is accepted for it; the scopes are those set for the app
/// (<c>meeting:write:meeting</c>, <c>user:read:user</c>).
/// </summary>
public static class ZoomAccount
{
    /// <summary>The app's public client id from marketplace.zoom.us → Manage → the app → App Credentials.</summary>
    public const string ClientId = "JSgW_4jaSaSeDoMXjqkGtg";

    public static OAuthEndpoints Endpoints { get; } = new(
        new Uri("https://zoom.us/oauth/authorize"),
        new Uri("https://zoom.us/oauth/token"),
        ClientId,
        [])
    {
        RedirectHost = "127.0.0.1",
        RedirectPath = "/callback",
        SelectAccount = false,
        ScopeOnTokenRequests = false,
    };
}

/// <summary>Zoom meetings through the Zoom API (scheduled meetings with the user's own settings – waiting room, passcode).</summary>
public sealed class ZoomMeetingProvider(HttpClient http, ICredentialStore credentials) : IMeetingProvider
{
    public const string UserKey = "zoom.user";

    private static readonly Uri Api = new("https://api.zoom.us/v2/");

    private readonly OAuthClient _oauth = new(http);

    public string Kind => MeetingKinds.Zoom;

    public bool IsAvailable => ZoomAccount.Endpoints.IsConfigured;

    public async Task<MeetingService> ConnectAsync(Guid id, string? server, IBrowserLauncher browser, CancellationToken cancellationToken = default)
    {
        try
        {
            var tokens = await _oauth.SignInAsync(ZoomAccount.Endpoints, null, browser, cancellationToken);
            await OAuthTokenSource.StoreAsync(credentials, id, tokens, cancellationToken);
            var me = await SendAsync(id, HttpMethod.Get, "users/me", null, cancellationToken);
            var user = Str(me, "email") ?? string.Empty;
            return new MeetingService(id, Kind, $"Zoom ({user})", new Dictionary<string, string> { [UserKey] = user });
        }
        catch (Exception ex) when (ex is OAuthException or HttpRequestException or MeetingException)
        {
            await credentials.DeleteSecretAsync(id, CancellationToken.None);
            throw ex as MeetingException ?? new MeetingException(ex.Message, inner: ex);
        }
    }

    public async Task<OnlineMeeting> CreateAsync(MeetingService service, MeetingRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(request);
        var meeting = await SendAsync(service.Id, HttpMethod.Post, "users/me/meetings", new
        {
            topic = string.IsNullOrWhiteSpace(request.Subject) ? T("Besprechung") : request.Subject,
            type = 2, // scheduled
            start_time = request.Start.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
            duration = Math.Max(1, (int)Math.Round((request.End - request.Start).TotalMinutes)),
        }, cancellationToken);
        var url = Str(meeting, "join_url") ?? throw new MeetingException(T("Zoom hat keinen Link zur Besprechung geliefert."));
        return new OnlineMeeting(Kind, url, DetailsOf(meeting));
    }

    public Task DisconnectAsync(MeetingService service, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(service);
        return credentials.DeleteSecretAsync(service.Id, cancellationToken);
    }

    // "Meeting-ID: 812 3456 7890" (as Zoom writes it) and the passcode.
    internal static string? DetailsOf(JsonElement meeting)
    {
        var details = new StringBuilder();
        if (meeting.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Number)
        {
            var digits = id.GetInt64().ToString(CultureInfo.InvariantCulture);
            var grouped = digits.Length > 6 ? $"{digits[..3]} {digits[3..^4]} {digits[^4..]}" : digits;
            details.Append(T("Besprechungs-ID:")).Append(' ').Append(grouped).Append('\n');
        }

        if (Str(meeting, "password") is { Length: > 0 } passcode)
        {
            details.Append(T("Kenncode:")).Append(' ').Append(passcode).Append('\n');
        }

        return details.Length == 0 ? null : details.ToString().TrimEnd();
    }

    private async Task<JsonElement> SendAsync(Guid id, HttpMethod method, string path, object? body, CancellationToken cancellationToken)
    {
        string token;
        try
        {
            token = await new OAuthTokenSource(_oauth, ZoomAccount.Endpoints, credentials, id).GetAccessTokenAsync(cancellationToken);
        }
        catch (OAuthException ex)
        {
            throw new MeetingException(ex.Message, ex.SignInRequired, ex);
        }

        using var request = new HttpRequestMessage(method, new Uri(Api, path)) { Content = body is null ? null : JsonContent.Create(body) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await http.SendAsync(request, cancellationToken);
        JsonElement answer;
        try
        {
            answer = response.Content.Headers.ContentLength is 0 ? default : await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        }
        catch (JsonException ex)
        {
            // A page instead of JSON (Zoom disturbed, a proxy in between).
            throw new MeetingException(F("Zoom hat abgelehnt: {0}", response.ReasonPhrase ?? ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture)), inner: ex);
        }

        if (!response.IsSuccessStatusCode)
        {
            var reason = answer.ValueKind == JsonValueKind.Object ? Str(answer, "message") : null;
            throw new MeetingException(F("Zoom hat abgelehnt: {0}", reason ?? response.ReasonPhrase ?? ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture)),
                signInRequired: response.StatusCode == System.Net.HttpStatusCode.Unauthorized);
        }

        return answer;
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
