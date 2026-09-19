using System.Net;
using System.Text;
using KidTime.Server.Services.Dns;
using Microsoft.Extensions.Logging.Abstractions;

namespace KidTime.Server.Tests;

/// <summary>
/// What KidTime makes of the companion's answers, and one answer in particular: the one that says
/// nothing at all.
///
/// The companion is a front for the DNS node. It signs in to that node once, on our login, and
/// holds the token in the session; a single moment of the node being unreachable is enough for the
/// node to reject it afterwards, and the companion then drops it and needs a fresh login to mint
/// another. Until that happens it goes on answering <c>200</c> - the request to the companion
/// itself succeeded - and simply leaves the node's half of the payload out. Read at face value
/// that is a household with no block lists and no site groups, which is a filtered home being told
/// its filter is off, with a fresh timestamp and nothing in the log. So: the missing payload
/// renews the session exactly as a 401 does, and if it is still missing the read fails rather than
/// answers.
/// </summary>
public sealed class TechnitiumCompanionClientTests
{
    private const string Blocking = """
        {"nodeId":"node1","config":{"enableBlocking":true,"groups":[
          {"name":"kid","enableBlocking":true,"blockListUrls":["https://example.test/ads.txt"],
           "adblockListUrls":[],"regexBlockListUrls":[]}]}}
        """;

    /// <summary>The same 200 the companion gives when the DNS node has rejected its token.</summary>
    private const string BlockingWithoutConfig = """{"nodeId":"node1","fetchedAt":"2026-09-19T16:06:55.000Z"}""";

    [Fact]
    public async Task SignsInAgainWhenTheNodeConfigurationIsMissingAndUsesTheRetry()
    {
        var handler = new StubHandler
        {
            Responses =
            {
                ["advanced-blocking/node1"] = [BlockingWithoutConfig, Blocking],
                ["nodes/dns-schedules/rules"] = ["[]"],
                ["domain-groups"] = ["[]"]
            }
        };

        var state = await Read(handler);

        Assert.True(state.Blocking.EnableBlocking);
        Assert.Equal("kid", Assert.Single(state.Blocking.Groups!).Name);
        // Once for the first read, once because the answer carried no node configuration. A
        // renewal is what mints a new node token, so it is the whole of the recovery.
        Assert.Equal(2, handler.Logins);
    }

    [Fact]
    public async Task FailsTheReadWhenTheNodeConfigurationIsStillMissingAfterSigningInAgain()
    {
        var handler = new StubHandler
        {
            Responses =
            {
                ["advanced-blocking/node1"] = [BlockingWithoutConfig, BlockingWithoutConfig],
                ["nodes/dns-schedules/rules"] = ["[]"],
                ["domain-groups"] = ["[]"]
            }
        };

        await Assert.ThrowsAsync<DnsCompanionException>(() => Read(handler));
        Assert.Equal(2, handler.Logins);
    }

    [Fact]
    public async Task SignsInAgainWhenTheCompanionsOwnSessionHasExpired()
    {
        var handler = new StubHandler
        {
            Responses =
            {
                ["advanced-blocking/node1"] = [Blocking],
                ["nodes/dns-schedules/rules"] = ["[]"],
                ["domain-groups"] = ["[]"]
            },
            UnauthorizedOnce = { "advanced-blocking/node1" }
        };

        var state = await Read(handler);

        Assert.True(state.Blocking.EnableBlocking);
        Assert.Equal(2, handler.Logins);
    }

    [Theory]
    [InlineData("nodes/dns-schedules/rules")]
    [InlineData("domain-groups")]
    public async Task FailsTheReadWhenAnyOtherPartOfItCannotBeRead(string path)
    {
        var handler = new StubHandler
        {
            Responses =
            {
                ["advanced-blocking/node1"] = [Blocking],
                ["nodes/dns-schedules/rules"] = ["[]"],
                ["domain-groups"] = ["[]"]
            },
            ServerErrors = { path }
        };

        // An empty list is a household with no timetables; a list that could not be read is not,
        // and the difference is the whole of the Internet tab going quietly blank.
        await Assert.ThrowsAsync<DnsCompanionException>(() => Read(handler));
    }

    [Fact]
    public async Task ReadsAHouseholdThatHasSwitchedFilteringOffAsAnAnswerRatherThanAFailure()
    {
        var handler = new StubHandler
        {
            Responses =
            {
                ["advanced-blocking/node1"] =
                    ["""{"nodeId":"node1","config":{"enableBlocking":false,"groups":[]}}"""],
                ["nodes/dns-schedules/rules"] = ["[]"],
                ["domain-groups"] = ["[]"]
            }
        };

        var state = await Read(handler);

        Assert.False(state.Blocking.EnableBlocking);
        Assert.Equal(1, handler.Logins);
    }

    private static async Task<TechnitiumCompanionClient.CompanionState> Read(StubHandler handler)
    {
        var options = new DnsFilteringOptions
        {
            ApiUrl = "https://companion.test:3443",
            Username = "admin",
            Password = "secret",
            NodeId = "node1"
        };
        using var client = new TechnitiumCompanionClient(
            options, NullLogger<TechnitiumCompanionClient>.Instance, handler);
        return await client.ReadAsync(CancellationToken.None);
    }

    /// <summary>The companion, as far as this client can tell one from the real thing.</summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        /// <summary>Bodies per path, taken in turn; the last one is repeated once it is reached.</summary>
        public Dictionary<string, List<string>> Responses { get; } = [];

        public HashSet<string> UnauthorizedOnce { get; } = [];
        public HashSet<string> ServerErrors { get; } = [];
        public int Logins { get; private set; }

        private readonly Dictionary<string, int> _served = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath["/api/".Length..];
            if (path == "auth/login")
            {
                Logins++;
                return Task.FromResult(Json(HttpStatusCode.Created, """{"authenticated":true}"""));
            }

            if (ServerErrors.Contains(path))
                return Task.FromResult(Json(HttpStatusCode.InternalServerError, "{}"));
            if (UnauthorizedOnce.Remove(path))
                return Task.FromResult(Json(HttpStatusCode.Unauthorized, """{"error":"Unauthorized"}"""));

            var bodies = Responses[path];
            var index = _served.TryGetValue(path, out var served) ? served : 0;
            _served[path] = index + 1;
            return Task.FromResult(Json(HttpStatusCode.OK, bodies[Math.Min(index, bodies.Count - 1)]));
        }

        private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
            new(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
    }
}
