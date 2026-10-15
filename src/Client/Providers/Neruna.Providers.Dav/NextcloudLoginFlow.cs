using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Neruna.Core.Auth;
using static Neruna.Core.Localization.Texts;

namespace Neruna.Providers.Dav;

/// <summary>What Nextcloud hands over after the sign-in: its address, the login name and an app password for Neruna.</summary>
public sealed record NextcloudLogin(Uri Server, string LoginName, string AppPassword)
{
    /// <summary>Calendars and contacts of Nextcloud (one DAV endpoint for both).</summary>
    public Uri DavUrl => new(NextcloudLoginFlow.WithSlash(Server), "remote.php/dav/");
}

/// <summary>A sign-in that did not finish (refused, timed out, not a Nextcloud).</summary>
public sealed class NextcloudLoginException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Nextcloud Login Flow v2: the user signs in in the browser (also with two-factor sign-in) and allows access;
/// Nextcloud then gives Neruna an app password of its own – it appears under Settings → Security → Devices &amp;
/// sessions as "Neruna" and can be revoked there. Neruna never sees the user's password.
/// </summary>
/// <seealso href="https://docs.nextcloud.com/server/latest/developer_manual/client_apis/LoginFlow/index.html#login-flow-v2"/>
public static class NextcloudLoginFlow
{
    /// <summary>Nextcloud keeps the request open for 20 minutes.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(20);

    /// <param name="http">Sends the requests; its User-Agent names the app password in Nextcloud.</param>
    /// <param name="server">"cloud.example.com", or the full address (also in a sub folder: https://example.com/nextcloud).</param>
    /// <exception cref="NextcloudLoginException">Not a Nextcloud, refused or not finished in time.</exception>
    public static async Task<NextcloudLogin> SignInAsync(HttpClient http, string server, IBrowserLauncher browser, TimeSpan? pollInterval = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(browser);
        var baseUrl = ParseServer(server) ?? throw new NextcloudLoginException(T("Bitte die Adresse der Nextcloud angeben, z. B. cloud.example.com."));

        // 1. Ask for a login: where the user signs in, and where Neruna asks for the result.
        Start start;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(baseUrl, "index.php/login/v2"));
            request.Headers.UserAgent.ParseAdd("Neruna");
            using var response = await http.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();
            start = await response.Content.ReadFromJsonAsync<Start>(cancellationToken) ?? throw new JsonException("empty");
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or NotSupportedException)
        {
            throw new NextcloudLoginException(F("Unter {0} antwortet keine Nextcloud: {1}", baseUrl.Host, ex.Message), ex);
        }

        if (start.Poll?.Token is not { Length: > 0 } token || !Uri.TryCreate(start.Poll.Endpoint, UriKind.Absolute, out var endpoint)
            || !Uri.TryCreate(start.Login, UriKind.Absolute, out var login) || login.Scheme != Uri.UriSchemeHttps && baseUrl.Scheme == Uri.UriSchemeHttps)
        {
            throw new NextcloudLoginException(F("Unter {0} antwortet keine Nextcloud.", baseUrl.Host));
        }

        // 2. The user signs in and allows access in the browser.
        await browser.OpenAsync(login, cancellationToken);

        // 3. Ask until Nextcloud has the result (404 until then).
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Lifetime);
        try
        {
            while (true)
            {
                await Task.Delay(pollInterval ?? TimeSpan.FromSeconds(2), timeout.Token);
                using var poll = new FormUrlEncodedContent([new KeyValuePair<string, string>("token", token)]);
                using var response = await http.PostAsync(endpoint, poll, timeout.Token);
                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    continue;
                }

                response.EnsureSuccessStatusCode();
                var result = await response.Content.ReadFromJsonAsync<Result>(timeout.Token);
                if (result is { LoginName.Length: > 0, AppPassword.Length: > 0 } && Uri.TryCreate(result.Server, UriKind.Absolute, out var granted))
                {
                    return new NextcloudLogin(WithSlash(granted), result.LoginName, result.AppPassword);
                }

                throw new NextcloudLoginException(T("Nextcloud hat keine Anmeldedaten geliefert."));
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new NextcloudLoginException(T("Die Anmeldung bei Nextcloud wurde nicht innerhalb von 20 Minuten abgeschlossen."));
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException)
        {
            throw new NextcloudLoginException(F("Anmeldung bei Nextcloud fehlgeschlagen: {0}", ex.Message), ex);
        }
    }

    /// <summary>"cloud.example.com" → https://cloud.example.com/; a full address keeps its path (Nextcloud in a sub folder).</summary>
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

        // Pasted from the browser: the part before Nextcloud's own pages.
        foreach (var marker in new[] { "/index.php", "/apps/", "/remote.php" })
        {
            var at = text.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (at > 0)
            {
                text = text[..at];
            }
        }

        return Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http" && uri.Host.Length > 0 ? WithSlash(uri) : null;
    }

    internal static Uri WithSlash(Uri url) => url.AbsoluteUri.EndsWith('/') ? url : new Uri(url.AbsoluteUri + "/");

    private sealed record Start(PollInfo? Poll, string? Login);

    private sealed record PollInfo(string? Token, string? Endpoint);

    private sealed record Result(string? Server, string? LoginName, string? AppPassword);
}
