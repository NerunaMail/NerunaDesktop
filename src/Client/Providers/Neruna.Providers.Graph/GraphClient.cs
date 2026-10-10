using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Neruna.Core;
using Neruna.Core.Auth;

namespace Neruna.Providers.Graph;

/// <summary>An answer from Graph that is not a success (status and Graph's error code and message).</summary>
public sealed class GraphException(HttpStatusCode status, string? code, string message) : Exception(message)
{
    public HttpStatusCode Status { get; } = status;

    public string? Code { get; } = code;
}

/// <summary>
/// The few things the providers need from Microsoft Graph (v1.0): JSON requests with the connection's access token,
/// paging through <c>@odata.nextLink</c>, one retry when Graph asks to wait (429/503).
/// </summary>
internal sealed class GraphClient(HttpClient http, OAuthTokenSource tokens)
{
    public static readonly Uri Base = new("https://graph.microsoft.com/v1.0/");

    /// <summary>Whether the sign-in includes <paramref name="scope"/> (permissions added later need a new sign-in).</summary>
    public Task<bool> GrantsAsync(string scope, CancellationToken cancellationToken) => tokens.GrantsAsync(scope, cancellationToken);

    public async Task<JsonElement> GetAsync(string pathOrUrl, CancellationToken cancellationToken, params (string Name, string Value)[] headers)
    {
        using var response = await SendAsync(HttpMethod.Get, pathOrUrl, null, cancellationToken, headers);
        return await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
    }

    public async Task<byte[]> GetBytesAsync(string path, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(HttpMethod.Get, path, null, cancellationToken);
        return await response.Content.ReadAsByteArrayAsync(cancellationToken);
    }

    /// <summary>Sends JSON (or nothing); the answer as JSON, or an empty element for 202/204.</summary>
    public async Task<JsonElement> SendJsonAsync(HttpMethod method, string path, object? body, CancellationToken cancellationToken, params (string Name, string Value)[] headers)
    {
        using var content = body is null ? null : JsonContent.Create(body);
        using var response = await SendAsync(method, path, content, cancellationToken, headers);
        return response.Content.Headers.ContentLength is 0 || response.StatusCode is HttpStatusCode.NoContent or HttpStatusCode.Accepted
            ? default
            : await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
    }

    /// <summary>A complete message as MIME (Graph takes it Base64 encoded as text/plain).</summary>
    public async Task<JsonElement> PostMimeAsync(string path, byte[] mime, CancellationToken cancellationToken)
    {
        using var content = new StringContent(Convert.ToBase64String(mime), Encoding.ASCII, "text/plain");
        using var response = await SendAsync(HttpMethod.Post, path, content, cancellationToken);
        return response.Content.Headers.ContentLength is 0 || response.StatusCode == HttpStatusCode.Accepted
            ? default
            : await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
    }

    /// <summary>All items of a collection, page by page; the last page's <c>@odata.deltaLink</c> (if any) is returned via <paramref name="deltaLink"/>.</summary>
    public async Task<List<JsonElement>> GetAllAsync(string pathOrUrl, CancellationToken cancellationToken, Action<string>? deltaLink = null, int max = int.MaxValue, params (string Name, string Value)[] headers)
    {
        var items = new List<JsonElement>();
        string? next = pathOrUrl;
        while (next is not null && items.Count < max)
        {
            var page = await GetAsync(next, cancellationToken, headers);
            if (page.TryGetProperty("value", out var value))
            {
                items.AddRange(value.EnumerateArray().Select(v => v.Clone()));
            }

            next = page.TryGetProperty("@odata.nextLink", out var link) ? link.GetString() : null;
            if (page.TryGetProperty("@odata.deltaLink", out var delta))
            {
                deltaLink?.Invoke(delta.GetString()!);
            }
        }

        return items;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string pathOrUrl, HttpContent? content, CancellationToken cancellationToken, params (string Name, string Value)[] headers)
    {
        var uri = pathOrUrl.StartsWith("https://", StringComparison.Ordinal) ? new Uri(pathOrUrl) : new Uri(Base, pathOrUrl);
        for (var attempt = 0; ; attempt++)
        {
            using var request = new HttpRequestMessage(method, uri) { Content = content };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await tokens.GetAccessTokenAsync(cancellationToken));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            foreach (var (name, value) in headers)
            {
                request.Headers.TryAddWithoutValidation(name, value);
            }

            var response = await http.SendAsync(request, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                return response;
            }

            // Throttled or busy: wait as told, once.
            if (attempt == 0 && response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable)
            {
                var wait = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(5);
                response.Dispose();
                await Task.Delay(wait < TimeSpan.FromSeconds(30) ? wait : TimeSpan.FromSeconds(30), cancellationToken);
                continue;
            }

            using (response)
            {
                string? code = null;
                var message = response.ReasonPhrase ?? response.StatusCode.ToString();
                try
                {
                    var error = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
                    if (error.TryGetProperty("error", out var e))
                    {
                        code = e.TryGetProperty("code", out var c) ? c.GetString() : null;
                        message = e.TryGetProperty("message", out var m) ? m.GetString() ?? message : message;
                    }
                }
                catch (JsonException)
                {
                    // Not Graph's error format.
                }

                if (response.StatusCode == HttpStatusCode.PreconditionFailed || code is "ErrorIrresolvableConflict")
                {
                    throw new RemoteConflictException();
                }

                throw new GraphException(response.StatusCode, code, message);
            }
        }
    }
}

internal static class Json
{
    public static string? Str(this JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public static bool Bool(this JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    public static int Int(this JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;

    public static JsonElement Obj(this JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) ? v : default;

    public static IEnumerable<JsonElement> Arr(this JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array ? v.EnumerateArray() : [];
}
