using System.Net;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Neruna.Core;
using Neruna.Core.Accounts;
using Neruna.Core.Calendar;
using Neruna.Core.Security;
using Neruna.Core.Providers;
using Neruna.Providers.Dav;

namespace Neruna.Client.Tests;

/// <summary>
/// SOGo lists calendars subscribed in its web interface only to clients whose User-Agent contains "Thunderbird"
/// (seen on a real server). This fake answers the same way.
/// </summary>
public class SogoSubscriptionTests
{
    [Fact]
    public async Task Subscribed_calendars_are_found_like_thunderbird_finds_them()
    {
        var ct = TestContext.Current.CancellationToken;
        var sogo = new FakeSogo();
        var http = new HttpClient(sogo);
        await using var env = await TestEnvironment.CreateAsync(services =>
            services.AddSingleton<IProviderFactory<ICalendarProvider>>(sp => new CalDavProviderFactory(http, sp.GetRequiredService<ICredentialStore>(), sp.GetRequiredService<ILoggerFactory>())));
        var connection = await env.AddAccountAsync(ServiceKind.Calendar, ProviderIds.CalDav, new DavSettings(new Uri("https://sogo.example/SOGo/dav/"), "anna@example.com").ToDictionary());

        var calendars = await env.Calendar.DiscoverAsync(connection, ct);

        Assert.Equal(["Lea (abonniert)", "Persönlich"], calendars.Select(c => c.Calendar.Name));
        Assert.All(sogo.UserAgents, ua => Assert.StartsWith("Neruna/", ua, StringComparison.Ordinal));
    }

    private sealed class FakeSogo : HttpMessageHandler
    {
        private const string Home = "/SOGo/dav/anna@example.com/Calendar/";

        public List<string> UserAgents { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var userAgent = request.Headers.UserAgent.ToString();
            UserAgents.Add(userAgent);
            var path = request.RequestUri!.AbsolutePath;
            var body = path switch
            {
                "/SOGo/dav/" => Response(path, "<D:resourcetype><D:collection/></D:resourcetype><C:calendar-home-set><D:href>" + Home + "</D:href></C:calendar-home-set>"),
                Home => Response(Home, "<D:resourcetype><D:collection/></D:resourcetype>")
                        + Calendar(Home + "personal/", "Persönlich")
                        + (userAgent.Contains("Thunderbird", StringComparison.Ordinal) ? Calendar(Home + "lea_A_example_D_com_personal/", "Lea (abonniert)") : string.Empty),
                _ => null,
            };

            return Task.FromResult(body is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage((HttpStatusCode)207)
                {
                    Content = new StringContent($"<?xml version=\"1.0\"?><D:multistatus xmlns:D=\"DAV:\" xmlns:C=\"urn:ietf:params:xml:ns:caldav\">{body}</D:multistatus>", Encoding.UTF8, "application/xml"),
                });
        }

        private static string Calendar(string href, string name) =>
            Response(href, $"<D:resourcetype><D:collection/><C:calendar/></D:resourcetype><D:displayname>{name}</D:displayname>");

        private static string Response(string href, string props) =>
            $"<D:response><D:href>{href}</D:href><D:propstat><D:prop>{props}</D:prop><D:status>HTTP/1.1 200 OK</D:status></D:propstat></D:response>";
    }
}
