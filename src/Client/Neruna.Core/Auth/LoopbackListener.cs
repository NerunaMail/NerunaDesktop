using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Web;

namespace Neruna.Core.Auth;

/// <summary>
/// Receives the browser's return after a sign-in on this computer only (RFC 8252 loopback redirect). Plain TCP instead
/// of HttpListener: on Windows, HttpListener may only listen on "localhost" without administrator rights, while some
/// providers require "127.0.0.1". For "localhost" both loopback addresses listen (browsers may pick either).
/// </summary>
internal sealed class LoopbackListener : IDisposable
{
    private readonly List<TcpListener> _listeners = [];

    private LoopbackListener(string host, int port)
    {
        Port = port;
        _listeners.Add(new TcpListener(IPAddress.Loopback, port));
        if (host == "localhost" && Socket.OSSupportsIPv6)
        {
            _listeners.Add(new TcpListener(IPAddress.IPv6Loopback, port));
        }

        foreach (var listener in _listeners.ToList())
        {
            try
            {
                listener.Start();
            }
            catch (SocketException) when (listener.LocalEndpoint.AddressFamily == AddressFamily.InterNetworkV6)
            {
                _listeners.Remove(listener); // no IPv6 loopback on this machine
            }
        }
    }

    public int Port { get; }

    /// <summary>Listens on a free port.</summary>
    public static LoopbackListener Start(string host)
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return new LoopbackListener(host, port);
    }

    /// <summary>One request: its path and query, and a way to answer it with a small page.</summary>
    public sealed class Request(TcpClient client, string path, System.Collections.Specialized.NameValueCollection query) : IDisposable
    {
        public string Path { get; } = path;

        public System.Collections.Specialized.NameValueCollection Query { get; } = query;

        public async Task RespondAsync(int status, string? html = null)
        {
            var body = Encoding.UTF8.GetBytes(html ?? string.Empty);
            var head = Encoding.ASCII.GetBytes(
                $"HTTP/1.1 {status} {(status == 200 ? "OK" : "Not Found")}\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
            var stream = client.GetStream();
            await stream.WriteAsync(head);
            await stream.WriteAsync(body);
            await stream.FlushAsync();
        }

        public void Dispose() => client.Dispose();
    }

    // One pending accept per listener, kept across calls: a new one would leave the old waiting, and it could take the
    // connection that matters.
    private readonly Dictionary<TcpListener, Task<TcpClient>> _accepts = [];

    public async Task<Request> NextAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            foreach (var listener in _listeners.Where(l => !_accepts.ContainsKey(l)))
            {
                _accepts[listener] = listener.AcceptTcpClientAsync(CancellationToken.None).AsTask();
            }

            var first = await Task.WhenAny(_accepts.Values).WaitAsync(cancellationToken);
            _accepts.Remove(_accepts.Single(a => a.Value == first).Key);
            var client = await first;
            var line = await ReadRequestLineAsync(client, cancellationToken);
            if (line is not null && line.Split(' ') is [_, var target, ..])
            {
                var questionMark = target.IndexOf('?', StringComparison.Ordinal);
                var path = questionMark < 0 ? target : target[..questionMark];
                var query = HttpUtility.ParseQueryString(questionMark < 0 ? string.Empty : target[(questionMark + 1)..]);
                return new Request(client, path, query);
            }

            client.Dispose();
        }
    }

    // Reads the head of the request (up to 16 KB, 10 s); only the request line matters.
    private static async Task<string?> ReadRequestLineAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var stream = client.GetStream();
        var buffer = new byte[16384];
        var length = 0;
        try
        {
            while (length < buffer.Length)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(length), timeout.Token);
                if (read == 0)
                {
                    break;
                }

                length += read;
                if (Encoding.ASCII.GetString(buffer, 0, length).Contains("\r\n\r\n", StringComparison.Ordinal))
                {
                    break;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return null;
        }

        var head = Encoding.ASCII.GetString(buffer, 0, length);
        var end = head.IndexOf("\r\n", StringComparison.Ordinal);
        return end < 0 ? null : head[..end];
    }

    public void Dispose()
    {
        foreach (var listener in _listeners)
        {
            listener.Dispose();
        }
    }
}
