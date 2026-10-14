using Neruna.Contracts.Discovery;
using Neruna.Core.Security;

namespace Neruna.Providers.Ews;

/// <summary>How Neruna signs in at the EWS address.</summary>
public enum EwsAuthentication
{
    /// <summary>What the server offers: Kerberos/NTLM (Negotiate) or Basic – the usual choice.</summary>
    Automatic,

    /// <summary>Basic, sent right away: for reverse proxies that do not pass Negotiate/NTLM through.</summary>
    Basic,

    /// <summary>No password: the client certificate alone (the proxy signs in at Exchange on the user's behalf).</summary>
    CertificateOnly,
}

/// <summary>
/// What an EWS connection stores (no secrets: the password lies in the keychain under the connection id, a client
/// certificate is referenced by <see cref="TlsCertificates.ClientCertificateKey"/>). Mail, calendar and contacts of an
/// account have the same settings.
/// </summary>
/// <param name="Url">The EWS address, normally https://mail.example.com/EWS/Exchange.asmx.</param>
/// <param name="Username">"anna@example.com", "EXAMPLE\anna" or "anna".</param>
/// <param name="Mailbox">The mailbox's address (who "I" am in meetings).</param>
public sealed record EwsSettings(Uri Url, string Username, string Mailbox, EwsAuthentication Authentication = EwsAuthentication.Automatic)
{
    public const string UrlKey = "ews.url";
    public const string UsernameKey = "username";
    public const string MailboxKey = "ews.mailbox";
    public const string AuthenticationKey = "ews.auth";

    /// <summary>The usual path of EWS on a server.</summary>
    public const string DefaultPath = "/EWS/Exchange.asmx";

    public IReadOnlyDictionary<string, string> ToDictionary()
    {
        var settings = new Dictionary<string, string> { [UrlKey] = Url.AbsoluteUri, [UsernameKey] = Username, [MailboxKey] = Mailbox };
        if (Authentication != EwsAuthentication.Automatic)
        {
            settings[AuthenticationKey] = Authentication.ToString();
        }

        return settings;
    }

    public static EwsSettings FromDictionary(IReadOnlyDictionary<string, string> settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!settings.TryGetValue(UrlKey, out var url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            throw new InvalidOperationException("EWS connection has no valid URL.");
        }

        return new EwsSettings(uri, settings.GetValueOrDefault(UsernameKey) ?? string.Empty, settings.GetValueOrDefault(MailboxKey) ?? string.Empty,
            Enum.TryParse<EwsAuthentication>(settings.GetValueOrDefault(AuthenticationKey), out var auth) ? auth : EwsAuthentication.Automatic);
    }

    /// <summary>
    /// "mail.example.com", "https://mail.example.com" or the full address → the EWS address (https, /EWS/Exchange.asmx
    /// when no path is given). Null if it is no usable address.
    /// </summary>
    public static Uri? ParseServer(string? input)
    {
        var text = input?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        if (!text.Contains("://", StringComparison.Ordinal))
        {
            text = "https://" + text;
        }

        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http") || uri.Host.Length == 0)
        {
            return null;
        }

        return uri.AbsolutePath is "/" or "" ? new Uri(uri, DefaultPath) : uri;
    }

    /// <summary>The EWS entry of a configuration (account dialog, later Neruna Cloud); null without one.</summary>
    internal static EwsSettings? FromDiscovery(MailProviderConfig config, string emailAddress)
    {
        ArgumentNullException.ThrowIfNull(config);
        return config.Ews is { } ews ? new EwsSettings(ews.ServerUrl, DiscoveryPlaceholders.Expand(ews.UsernameTemplate, emailAddress), emailAddress) : null;
    }
}
