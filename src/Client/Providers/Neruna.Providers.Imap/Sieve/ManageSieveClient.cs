using System.Globalization;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using static Neruna.Core.Localization.Texts;

namespace Neruna.Providers.Imap.Sieve;

/// <summary>The server answered NO or BYE; <see cref="Exception.Message"/> is the server's text.</summary>
internal sealed class ManageSieveException(string message) : Exception(message);

/// <summary>
/// A minimal ManageSieve client (RFC 5804): STARTTLS, SASL PLAIN, list, get, put and activate scripts. The password only
/// travels encrypted, unless the caller explicitly allows plain text (a test server without TLS).
/// </summary>
internal sealed class ManageSieveClient : IAsyncDisposable
{
    private readonly TcpClient _tcp;
    private Stream _stream;
    private Dictionary<string, string> _capabilities = new(StringComparer.OrdinalIgnoreCase);

    private ManageSieveClient(TcpClient tcp)
    {
        _tcp = tcp;
        _stream = Stream.Null; // the network stream once connected
    }

    /// <summary>The Sieve extensions the server supports ("vacation", "date", …).</summary>
    public IReadOnlySet<string> Extensions { get; private set; } = new HashSet<string>();

    public bool IsEncrypted => _stream is SslStream;

    /// <param name="allowPlainText">Log in without TLS if the server offers none (only where IMAP is unencrypted too).</param>
    public static async Task<ManageSieveClient> ConnectAsync(string host, int port, bool allowPlainText, CancellationToken cancellationToken)
    {
        var tcp = new TcpClient();
        var client = new ManageSieveClient(tcp);
        try
        {
            await tcp.ConnectAsync(host, port, cancellationToken);
            client._stream = tcp.GetStream();
            await client.ReadCapabilitiesAsync(cancellationToken);
            if (client._capabilities.ContainsKey("STARTTLS"))
            {
                await client.CommandAsync("STARTTLS", cancellationToken);
                var tls = new SslStream(client._stream, leaveInnerStreamOpen: false);
                await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = host }, cancellationToken);
                client._stream = tls;
                await client.ReadCapabilitiesAsync(cancellationToken); // the server says them again after TLS
            }
            else if (!allowPlainText)
            {
                throw new ManageSieveException(T("Der Server bietet keine verschlüsselte Verbindung (STARTTLS) an."));
            }

            return client;
        }
        catch
        {
            await client.DisposeAsync();
            throw;
        }
    }

    public async Task AuthenticateAsync(string user, string password, CancellationToken cancellationToken)
    {
        if (!_capabilities.TryGetValue("SASL", out var mechanisms) || !mechanisms.Split(' ').Contains("PLAIN", StringComparer.OrdinalIgnoreCase))
        {
            throw new ManageSieveException(T("Der Server bietet keine Anmeldung mit Benutzername und Passwort (PLAIN) an."));
        }

        var initial = Convert.ToBase64String(Encoding.UTF8.GetBytes("\0" + user + "\0" + password));
        await CommandAsync($"AUTHENTICATE \"PLAIN\" \"{initial}\"", cancellationToken);
    }

    /// <returns>The scripts and which one is active (at most one).</returns>
    public async Task<IReadOnlyList<(string Name, bool Active)>> ListScriptsAsync(CancellationToken cancellationToken)
    {
        await WriteAsync("LISTSCRIPTS\r\n", cancellationToken);
        var result = new List<(string, bool)>();
        while (true)
        {
            var line = await ReadLineAsync(cancellationToken);
            if (IsFinal(line))
            {
                EnsureOk(line);
                return result;
            }

            var tokens = Tokens(line);
            var name = tokens.Count > 0 && tokens[0].StartsWith('{') ? await ReadLiteralAsync(tokens[0], cancellationToken) : tokens.FirstOrDefault() ?? string.Empty;
            result.Add((name, tokens.Skip(1).Any(t => t.Equals("ACTIVE", StringComparison.OrdinalIgnoreCase))));
        }
    }

    public async Task<string> GetScriptAsync(string name, CancellationToken cancellationToken)
    {
        await WriteAsync($"GETSCRIPT {Quote(name)}\r\n", cancellationToken);
        var line = await ReadLineAsync(cancellationToken);
        if (IsFinal(line))
        {
            EnsureOk(line);
            return string.Empty;
        }

        var script = await ReadLiteralAsync(line.Trim(), cancellationToken);
        EnsureOk(await ReadFinalAsync(cancellationToken));
        return script;
    }

    /// <exception cref="ManageSieveException">The server refused the script (syntax, quota) – its reason in the message.</exception>
    public async Task PutScriptAsync(string name, string script, CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(script);
        await WriteAsync($"PUTSCRIPT {Quote(name)} {{{bytes.Length.ToString(CultureInfo.InvariantCulture)}+}}\r\n", cancellationToken);
        await _stream.WriteAsync(bytes, cancellationToken);
        await WriteAsync("\r\n", cancellationToken);
        EnsureOk(await ReadFinalAsync(cancellationToken));
    }

    public Task SetActiveAsync(string name, CancellationToken cancellationToken) => CommandAsync($"SETACTIVE {Quote(name)}", cancellationToken);

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_tcp.Connected)
            {
                using var quick = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await WriteAsync("LOGOUT\r\n", quick.Token);
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException or SocketException)
        {
            // Closing anyway.
        }

        await _stream.DisposeAsync();
        _tcp.Dispose();
    }

    private async Task ReadCapabilitiesAsync(CancellationToken cancellationToken)
    {
        var capabilities = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        while (true)
        {
            var line = await ReadLineAsync(cancellationToken);
            if (IsFinal(line))
            {
                EnsureOk(line);
                break;
            }

            var tokens = Tokens(line);
            if (tokens.Count > 0)
            {
                capabilities[tokens[0]] = tokens.Count > 1 ? tokens[1] : string.Empty;
            }
        }

        _capabilities = capabilities;
        Extensions = (capabilities.GetValueOrDefault("SIEVE") ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private async Task CommandAsync(string command, CancellationToken cancellationToken)
    {
        await WriteAsync(command + "\r\n", cancellationToken);
        EnsureOk(await ReadFinalAsync(cancellationToken));
    }

    private async Task<string> ReadFinalAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            var line = await ReadLineAsync(cancellationToken);
            if (IsFinal(line))
            {
                return line;
            }
        }
    }

    private static bool IsFinal(string line) =>
        line.StartsWith("OK", StringComparison.OrdinalIgnoreCase) || line.StartsWith("NO", StringComparison.OrdinalIgnoreCase) || line.StartsWith("BYE", StringComparison.OrdinalIgnoreCase);

    private static void EnsureOk(string line)
    {
        if (!line.StartsWith("OK", StringComparison.OrdinalIgnoreCase))
        {
            var text = Tokens(line).Skip(1).LastOrDefault(t => !t.StartsWith('(')) ?? line;
            throw new ManageSieveException(text);
        }
    }

    private Task WriteAsync(string text, CancellationToken cancellationToken) =>
        _stream.WriteAsync(Encoding.UTF8.GetBytes(text), cancellationToken).AsTask();

    private async Task<string> ReadLineAsync(CancellationToken cancellationToken)
    {
        var bytes = new List<byte>(128);
        var one = new byte[1];
        while (true)
        {
            if (await _stream.ReadAsync(one, cancellationToken) == 0)
            {
                throw new IOException("The Sieve server closed the connection.");
            }

            if (one[0] == '\n')
            {
                break;
            }

            if (bytes.Count > 65536)
            {
                throw new IOException("Line too long.");
            }

            bytes.Add(one[0]);
        }

        if (bytes.Count > 0 && bytes[^1] == '\r')
        {
            bytes.RemoveAt(bytes.Count - 1);
        }

        return Encoding.UTF8.GetString([.. bytes]);
    }

    // "{123}" (or "{123+}"): that many octets follow, then the line goes on.
    private async Task<string> ReadLiteralAsync(string token, CancellationToken cancellationToken)
    {
        var length = int.Parse(token.Trim('{', '}', '+'), NumberStyles.None, CultureInfo.InvariantCulture);
        if (length > 1024 * 1024)
        {
            throw new IOException("Script too large.");
        }

        var buffer = new byte[length];
        await _stream.ReadExactlyAsync(buffer, cancellationToken);
        await ReadLineAsync(cancellationToken); // rest of the line (CRLF)
        return Encoding.UTF8.GetString(buffer);
    }

    // Quoted strings and atoms of one response line.
    private static List<string> Tokens(string line)
    {
        var tokens = new List<string>();
        var i = 0;
        while (i < line.Length)
        {
            if (line[i] == ' ')
            {
                i++;
            }
            else if (line[i] == '"')
            {
                var value = new StringBuilder();
                i++;
                while (i < line.Length && line[i] != '"')
                {
                    if (line[i] == '\\' && i + 1 < line.Length)
                    {
                        i++;
                    }

                    value.Append(line[i++]);
                }

                tokens.Add(value.ToString());
                i++;
            }
            else
            {
                var start = i;
                while (i < line.Length && line[i] != ' ')
                {
                    i++;
                }

                tokens.Add(line[start..i]);
            }
        }

        return tokens;
    }

    private static string Quote(string value) => "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
}
