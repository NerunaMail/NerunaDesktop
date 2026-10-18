using System.Security.Cryptography;
using System.Text.Json;
using Neruna.Core.Auth;
using Neruna.Core.Calendar;
using static Neruna.Core.Localization.Texts;

namespace Neruna.Core.Meetings;

/// <summary>A choice for an event's online meeting: a set-up service, or the calendar's own server (Teams through Exchange).</summary>
/// <param name="Service">Null for the calendar's server: <see cref="Kind"/> is then created when the event is saved.</param>
public sealed record MeetingChoice(string Kind, string Name, MeetingService? Service);

/// <summary>
/// The meeting services (setting "meetings.services", per device like the tokens they use) and creating meetings
/// with them. A calendar of a Microsoft 365 account offers Teams itself (<see cref="ICalendarOnlineMeetings"/>); then
/// Exchange creates the meeting and nothing has to be set up.
/// </summary>
public sealed class MeetingController(ISettingsStore settings, IEnumerable<IMeetingProvider> providers, CalendarController calendars)
{
    public const string ServicesKey = "meetings.services";

    private readonly IReadOnlyList<IMeetingProvider> _providers = [.. providers];

    /// <summary>The kinds that can be connected in this version (Zoom needs an app registration).</summary>
    public IReadOnlyList<string> AvailableKinds => [.. _providers.Where(p => p.IsAvailable).Select(p => p.Kind)];

    public bool IsAvailable(string kind) => _providers.Any(p => p.Kind == kind && p.IsAvailable);

    public async Task<IReadOnlyList<MeetingService>> GetServicesAsync(CancellationToken cancellationToken = default)
    {
        var json = await settings.GetAsync(ServicesKey, cancellationToken);
        try
        {
            return json is null ? [] : JsonSerializer.Deserialize<List<MeetingService>>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>Signs in (or checks the server) and adds the service.</summary>
    /// <exception cref="MeetingException">It could not be connected.</exception>
    public async Task<MeetingService> AddAsync(string kind, string? server, IBrowserLauncher browser, CancellationToken cancellationToken = default)
    {
        var provider = ProviderOf(kind);
        var service = await provider.ConnectAsync(Guid.NewGuid(), server, browser, cancellationToken);
        var services = (await GetServicesAsync(cancellationToken)).ToList();

        // The same sign-in or server again replaces the old entry (and its token).
        foreach (var same in services.Where(s => s.Kind == service.Kind && s.Name == service.Name).ToList())
        {
            await provider.DisconnectAsync(same, cancellationToken);
            services.Remove(same);
        }

        services.Add(service);
        await SaveAsync(services, cancellationToken);
        return service;
    }

    public async Task RemoveAsync(MeetingService service, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(service);
        if (_providers.FirstOrDefault(p => p.Kind == service.Kind) is { } provider)
        {
            await provider.DisconnectAsync(service, cancellationToken);
        }

        await SaveAsync([.. (await GetServicesAsync(cancellationToken)).Where(s => s.Id != service.Id)], cancellationToken);
    }

    /// <summary>What an event in <paramref name="calendar"/> can get: the calendar's own Teams first, then the services.</summary>
    public async Task<IReadOnlyList<MeetingChoice>> GetChoicesAsync(CalendarInfo? calendar, CancellationToken cancellationToken = default)
    {
        var choices = new List<MeetingChoice>();
        if (calendar is not null)
        {
            foreach (var kind in await calendars.GetOnlineMeetingKindsAsync(calendar, cancellationToken))
            {
                choices.Add(new MeetingChoice(kind, F("{0} (über dieses Konto)", MeetingKinds.NameOf(kind)), null));
            }
        }

        choices.AddRange((await GetServicesAsync(cancellationToken)).Where(s => _providers.Any(p => p.Kind == s.Kind))
            .Select(s => new MeetingChoice(s.Kind, s.Name, s)));
        return choices;
    }

    /// <summary>
    /// The meeting for <paramref name="choice"/>: created now with a service, or pending (the calendar's server creates it
    /// when the event is saved).
    /// </summary>
    /// <exception cref="MeetingException">The service refused.</exception>
    public async Task<OnlineMeeting> CreateAsync(MeetingChoice choice, MeetingRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(choice);
        return choice.Service is { } service
            ? await ProviderOf(service.Kind).CreateAsync(service, request, cancellationToken)
            : new OnlineMeeting(choice.Kind, null);
    }

    private IMeetingProvider ProviderOf(string kind) =>
        _providers.FirstOrDefault(p => p.Kind == kind) ?? throw new MeetingException(T("Dieser Besprechungsdienst wird von dieser Version nicht unterstützt."));

    private Task SaveAsync(IReadOnlyList<MeetingService> services, CancellationToken cancellationToken) =>
        settings.SetAsync(ServicesKey, JsonSerializer.Serialize(services), cancellationToken);
}

/// <summary>
/// Jitsi Meet on meet.jit.si or your own server: a meeting is just a room on the server, so no sign-in and no request –
/// the link is the server's address and a random room name nobody can guess.
/// </summary>
public sealed class JitsiMeetingProvider(HttpClient http) : IMeetingProvider
{
    public const string ServerKey = "jitsi.server";

    public const string DefaultServer = "https://meet.jit.si";

    public string Kind => MeetingKinds.Jitsi;

    public bool IsAvailable => true;

    public async Task<MeetingService> ConnectAsync(Guid id, string? server, IBrowserLauncher browser, CancellationToken cancellationToken = default)
    {
        var url = Normalize(string.IsNullOrWhiteSpace(server) ? DefaultServer : server)
                  ?? throw new MeetingException(T("Bitte die Adresse des Jitsi-Servers angeben, z. B. https://meet.example.org."));
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            using var response = await http.GetAsync(url, timeout.Token);
            if ((int)response.StatusCode >= 500 || response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                throw new MeetingException(F("Der Server {0} antwortet nicht wie ein Jitsi-Server ({1}).", url.Host, (int)response.StatusCode));
            }
        }
        catch (Exception ex) when (ex is HttpRequestException || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            throw new MeetingException(F("Der Server {0} ist nicht erreichbar: {1}", url.Host, ex.Message), inner: ex);
        }

        var address = url.GetLeftPart(UriPartial.Path).TrimEnd('/');
        return new MeetingService(id, Kind, $"{MeetingKinds.NameOf(Kind)} ({url.Host})", new Dictionary<string, string> { [ServerKey] = address });
    }

    public Task<OnlineMeeting> CreateAsync(MeetingService service, MeetingRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(service);
        var server = service.Settings.GetValueOrDefault(ServerKey) ?? DefaultServer;
        return Task.FromResult(new OnlineMeeting(Kind, $"{server.TrimEnd('/')}/{RoomName()}"));
    }

    public Task DisconnectAsync(MeetingService service, CancellationToken cancellationToken = default) => Task.CompletedTask;

    /// <summary>"Neruna" and 16 random letters/digits (80 bits): unguessable, and still readable when spelled out.</summary>
    internal static string RoomName()
    {
        const string alphabet = "abcdefghijkmnpqrstuvwxyz23456789";
        return "Neruna" + string.Concat(RandomNumberGenerator.GetBytes(16).Select(b => alphabet[b % alphabet.Length]));
    }

    // "meet.example.org" → https://meet.example.org/; only http(s).
    private static Uri? Normalize(string server)
    {
        var text = server.Trim();
        if (!text.Contains("://", StringComparison.Ordinal))
        {
            text = "https://" + text;
        }

        return Uri.TryCreate(text, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp) && uri.Host.Length > 0
            ? uri
            : null;
    }
}
