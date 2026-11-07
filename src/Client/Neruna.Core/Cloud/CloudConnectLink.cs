using System.Web;

namespace Neruna.Core.Cloud;

/// <summary>
/// "neruna://connect?server=https%3A%2F%2Fportal.example.ch&amp;code=K7Q2-…": the button in the mail with the connection
/// code (via the server's page /connect) opens Neruna with server and code filled in – the PIN still comes separately.
/// Nothing connects by itself: the person sees the server and confirms with the PIN.
/// </summary>
public sealed record CloudConnectLink(Uri Server, string? Code)
{
    private const string Prefix = "neruna://connect";

    public static bool IsConnectLink(string? item) =>
        item is not null && item.Trim().StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)
        && (item.Trim().Length == Prefix.Length || item.Trim()[Prefix.Length] is '?' or '/');

    /// <summary>The server (https, or http on this computer) and the code as typed in the app; null if not a valid link.</summary>
    public static CloudConnectLink? TryParse(string? item)
    {
        if (!IsConnectLink(item) || !Uri.TryCreate(item!.Trim(), UriKind.Absolute, out var uri))
        {
            return null;
        }

        var query = HttpUtility.ParseQueryString(uri.Query);
        if (query["server"] is not { Length: > 0 and <= 512 } server)
        {
            return null;
        }

        Uri parsed;
        try
        {
            parsed = CloudController.ParseServer(server);
        }
        catch (CloudException)
        {
            return null;
        }

        // A connection code: Crockford Base32 with dashes; anything else is left out rather than shown.
        var code = query["code"]?.Trim();
        if (code is not { Length: > 0 and <= 64 } || !code.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'))
        {
            code = null;
        }

        return new CloudConnectLink(parsed, code?.ToUpperInvariant());
    }

    /// <summary>The server as shown in the form (without the trailing slash).</summary>
    public string ServerText => Server.GetLeftPart(UriPartial.Path).TrimEnd('/');

    public string ToLink() =>
        $"{Prefix}?server={Uri.EscapeDataString(ServerText)}" + (Code is { Length: > 0 } c ? "&code=" + Uri.EscapeDataString(c) : string.Empty);
}
