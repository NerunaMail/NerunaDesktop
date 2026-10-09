namespace Neruna.Contracts.Cloud;

/// <summary>Body of <c>POST /api/v1/enrollment</c>: one-time code + PIN, the device's public key and what the portal shows about it.</summary>
/// <param name="PublicKey">ECDSA P-256 SubjectPublicKeyInfo (PEM).</param>
/// <param name="OsUser">The account signed in on the computer, e.g. <c>DOMAIN\anna</c>.</param>
public sealed record EnrollmentRequest(
    string Code,
    string Pin,
    string PublicKey,
    string DeviceName,
    string? AppVersion,
    string? OsName,
    string? OsVersion,
    string? OsUser);

/// <summary>Response of <c>POST /api/v1/enrollment</c>.</summary>
public sealed record EnrollmentResponse(string DeviceId, string OrganizationName, string MemberName);

/// <summary>Body of <c>POST /api/v1/token</c>: a JWT signed with the device key (ES256).</summary>
public sealed record TokenRequest(string Assertion);

/// <summary>Response of <c>POST /api/v1/token</c>.</summary>
public sealed record TokenResponse(string AccessToken, string TokenType, int ExpiresIn);

/// <summary>Refused request: machine-readable <see cref="Error"/>, German <see cref="Message"/> for the user.
/// Input validation answers carry only a message (no code).</summary>
public sealed record CloudError(string? Error, string Message);

/// <summary>Response of <c>GET /api/v1/me</c>: the connected person, the organisation, its add-ons and this device.</summary>
public sealed record MeResponse(CloudMember Member, CloudOrganization Organization, IReadOnlyList<string> Addons, CloudDevice Device);

public sealed record CloudMember(
    string Id,
    string FirstName,
    string LastName,
    string Email,
    string? Position,
    string? Department,
    string? PhoneDirect,
    string? PhoneMobile,
    bool HasPhoto);

public sealed record CloudOrganization(
    string Name,
    string? Street,
    string? PostalCode,
    string? City,
    string? Country,
    string? Phone,
    string? Email,
    string? Website);

public sealed record CloudDevice(string Id, string Name);

/// <summary>Response of <c>GET /api/v1/signatures</c>: the organisation's signatures, filled in for this person.</summary>
/// <param name="Available">The licence includes central signatures.</param>
public sealed record SignaturesResponse(bool Available, IReadOnlyList<CloudSignature> Signatures);

/// <param name="Html">Mail-safe HTML (inline styles, images as data: URIs).</param>
/// <param name="UpdatedAt">Also moves when the person's or the organisation's details change.</param>
public sealed record CloudSignature(string Id, string Name, string Html, string Text, DateTimeOffset UpdatedAt);

/// <summary>Response of <c>GET /api/v1/text-templates</c>: the organisation's text templates, filled in for this person.</summary>
public sealed record TextTemplatesResponse(bool Available, IReadOnlyList<CloudTextTemplate> Templates);

/// <param name="Shortcut">Typed with "::" while writing (may be null).</param>
public sealed record CloudTextTemplate(string Id, string Name, string? Shortcut, string Html, string Text, DateTimeOffset UpdatedAt);

/// <summary>
/// Response of <c>GET /api/v1/chat?after=…</c>: rooms the person sees, everyone of the organisation with their online
/// status, the messages newer than <c>after</c> (at most 500; <see cref="More"/> → ask again) and the read markers.
/// </summary>
/// <param name="Available">The licence includes the chat (otherwise everything is empty).</param>
/// <param name="RetentionDays">Messages older than this are deleted on the server; the app deletes them too.</param>
/// <param name="Me">The connected person's id.</param>
/// <param name="Reads">Conversation ("room:&lt;id&gt;", "pm:&lt;person id&gt;") → id of the last message read.</param>
public sealed record ChatResponse(
    bool Available,
    int RetentionDays,
    string Me,
    IReadOnlyList<CloudChatRoom> Rooms,
    IReadOnlyList<CloudChatPerson> People,
    IReadOnlyList<CloudChatMessage> Messages,
    IReadOnlyDictionary<string, long> Reads,
    bool More);

/// <param name="MemberIds">The people who see the room.</param>
public sealed record CloudChatRoom(string Id, string Name, string? Description, IReadOnlyList<string> MemberIds);

/// <param name="Presence">available, away, brb, dnd or offline (also when the app has not called in for 5 minutes).</param>
public sealed record CloudChatPerson(string Id, string Name, string? Position, string Presence);

/// <summary>A chat message in a room (<see cref="RoomId"/>) or private (<see cref="RecipientId"/>).</summary>
/// <param name="Text">Plain text with **bold** and __underlined__; emoji as Unicode.</param>
public sealed record CloudChatMessage(long Id, string? RoomId, string SenderId, string? RecipientId, string Text, DateTimeOffset SentAt);

/// <summary>Body of <c>POST /api/v1/chat/messages</c>: either a room or a recipient.</summary>
public sealed record ChatSendRequest(string? RoomId, string? RecipientId, string Text);

/// <summary>Body of <c>POST /api/v1/chat/read</c>.</summary>
public sealed record ChatReadRequest(string Conversation, long LastId);

/// <summary>Body of <c>PUT /api/v1/presence</c>.</summary>
public sealed record PresenceRequest(string Presence);
