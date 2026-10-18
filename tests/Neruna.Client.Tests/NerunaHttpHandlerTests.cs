using System.Net;
using Microsoft.Extensions.Logging;
using Neruna.Core.Diagnostics;

namespace Neruna.Client.Tests;

public class NerunaHttpHandlerTests
{
    private sealed class Answer(HttpStatusCode status) : HttpMessageHandler
    {
        public HttpRequestMessage? Seen { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Seen = request;
            var response = new HttpResponseMessage(status) { Content = new StringContent("<html>Too Many Requests – Hoster</html>") };
            response.Headers.TryAddWithoutValidation("Server", "nginx");
            response.Headers.TryAddWithoutValidation("Retry-After", "30");
            return Task.FromResult(response);
        }
    }

    private sealed class Lines : ILogger
    {
        public List<string> Logged { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Logged.Add(formatter(state, exception));
    }

    [Fact]
    public async Task The_user_agent_goes_only_to_neruna_servers_and_a_429_is_logged_in_detail()
    {
        var inner = new Answer(HttpStatusCode.TooManyRequests);
        var log = new Lines();
        using var client = new HttpClient(new NerunaHttpHandler("Neruna-Desktop/0.13.0", log) { InnerHandler = inner });

        using var own = await client.GetAsync(new Uri("https://neruna.cloud/api/v1/phishing/hosts?since=123"), TestContext.Current.CancellationToken);
        Assert.Equal("Neruna-Desktop/0.13.0", inner.Seen!.Headers.UserAgent.ToString());
        Assert.Contains(log.Logged, l => l.Contains("429", StringComparison.Ordinal) && l.Contains("Server: nginx", StringComparison.Ordinal)
                                         && l.Contains("Retry-After: 30", StringComparison.Ordinal) && l.Contains("Hoster", StringComparison.Ordinal)
                                         && !l.Contains("since=", StringComparison.Ordinal));
        Assert.Contains("Hoster", await own.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal); // still readable

        using var other = await client.GetAsync(new Uri("https://newsletter.example/pixel.gif"), TestContext.Current.CancellationToken);
        Assert.Empty(inner.Seen!.Headers.UserAgent);
    }
}
