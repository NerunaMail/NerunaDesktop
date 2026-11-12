using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Neruna.Providers.Dav;

namespace Neruna.Client.Tests;

/// <summary>
/// A save over a kept connection that the server had already closed never reaches the server ("the response ended
/// prematurely"): it is sent once more on a new connection – only with a precondition, and only once.
/// </summary>
public class DavPutRetryTests
{
    private sealed class Handler(Func<int, HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Bodies.Add(request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken));
            return answer(Calls, request);
        }
    }

    private static readonly Uri Url = new("http://dav.example/cal/a.ics");

    private static HttpRequestException Closed() =>
        new(HttpRequestError.ResponseEnded, "An error occurred while sending the request.", new HttpIOException(HttpRequestError.ResponseEnded, "The response ended prematurely."));

    private static DavClient Client(Handler handler) => new(new HttpClient(handler), "anna", "geheim", NullLogger.Instance);

    [Fact]
    public async Task A_create_the_server_never_answered_is_sent_again_with_the_same_content_and_condition()
    {
        var handler = new Handler((call, request) =>
        {
            Assert.Equal("*", request.Headers.IfNoneMatch.ToString());
            return call == 1 ? throw Closed() : new HttpResponseMessage(HttpStatusCode.Created);
        });

        var response = await Client(handler).PutAsync(Url, "BEGIN:VCALENDAR", "text/calendar", null, create: true, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Created, response.Status);
        Assert.Equal(["BEGIN:VCALENDAR", "BEGIN:VCALENDAR"], handler.Bodies);
    }

    [Fact]
    public async Task It_is_tried_again_only_once()
    {
        var handler = new Handler((_, _) => throw Closed());

        await Assert.ThrowsAsync<HttpRequestException>(() => Client(handler).PutAsync(Url, "x", "text/calendar", "\"1\"", create: false, TestContext.Current.CancellationToken));

        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task Without_a_condition_or_with_another_error_a_save_is_not_repeated()
    {
        var unconditional = new Handler((_, _) => throw Closed());
        await Assert.ThrowsAsync<HttpRequestException>(() => Client(unconditional).PutAsync(Url, "x", "text/calendar", null, create: false, TestContext.Current.CancellationToken));
        Assert.Equal(1, unconditional.Calls);

        // Cut off in the middle of an answer: the server did get the request.
        var midway = new Handler((_, _) => throw new HttpRequestException("An error occurred while sending the request.", new IOException("Unable to read data from the transport connection.")));
        await Assert.ThrowsAsync<HttpRequestException>(() => Client(midway).PutAsync(Url, "x", "text/calendar", null, create: true, TestContext.Current.CancellationToken));
        Assert.Equal(1, midway.Calls);
    }
}
