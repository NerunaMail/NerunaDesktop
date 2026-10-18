using Neruna.Core.Auth;

namespace Neruna.Core.Meetings;

/// <summary>
/// A meeting service the user set up (Einstellungen → Besprechungen): a Microsoft sign-in for Teams, a Zoom sign-in,
/// a Jitsi server … Several of one kind are possible (two Jitsi servers). No secrets: tokens are in the keychain
/// under <see cref="Id"/>.
/// </summary>
/// <param name="Kind">One of <see cref="MeetingKinds"/>; picks the provider.</param>
/// <param name="Name">Shown in lists: "Microsoft Teams (anna@example.com)", "Jitsi Meet (meet.example.org)".</param>
public sealed record MeetingService(Guid Id, string Kind, string Name, IReadOnlyDictionary<string, string> Settings);

/// <summary>What the meeting is for: title and time (Zoom and Teams show them; a Jitsi room needs neither).</summary>
public sealed record MeetingRequest(string Subject, DateTimeOffset Start, DateTimeOffset End);

/// <summary>A message for the user; <see cref="SignInRequired"/> when the service has to be connected again.</summary>
public sealed class MeetingException(string message, bool signInRequired = false, Exception? inner = null) : Exception(message, inner)
{
    public bool SignInRequired { get; } = signInRequired;
}

/// <summary>
/// Creates online meetings with one kind of service. Requests go straight from this device to the service; Neruna's
/// servers are never involved.
/// </summary>
public interface IMeetingProvider
{
    /// <summary>One of <see cref="MeetingKinds"/>.</summary>
    string Kind { get; }

    /// <summary>Whether this version of Neruna can connect it (an app registration with the service exists).</summary>
    bool IsAvailable { get; }

    /// <summary>Signs in (in the browser) or checks the settings, and returns the service to keep.</summary>
    /// <param name="server">The server's address, for services that run on your own server (Jitsi); otherwise ignored.</param>
    /// <exception cref="MeetingException">The sign-in failed or the server cannot be used.</exception>
    Task<MeetingService> ConnectAsync(Guid id, string? server, IBrowserLauncher browser, CancellationToken cancellationToken = default);

    /// <exception cref="MeetingException">The service refused (with its reason).</exception>
    Task<OnlineMeeting> CreateAsync(MeetingService service, MeetingRequest request, CancellationToken cancellationToken = default);

    /// <summary>Forgets the sign-in of <paramref name="service"/> (its tokens in the keychain).</summary>
    Task DisconnectAsync(MeetingService service, CancellationToken cancellationToken = default);
}
