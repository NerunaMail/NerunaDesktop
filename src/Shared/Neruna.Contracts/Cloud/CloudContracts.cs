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
