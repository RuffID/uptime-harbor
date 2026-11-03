using System.Net;
using System.Net.Http.Headers;
using System.Text;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Infrastructure.Cluster.Onboarding;
using UptimeHarbor.Infrastructure.Installation;
using Xunit;

namespace UptimeHarbor.Tests.Infrastructure.Installation;

public class EnrollmentTransportTests
{
    [Fact]
    public async Task ReserveUsesOnlySelectedOriginAndNeverSendsSecretInUriOrHeaders()
    {
        // Arrange
        Handler handler = new();
        using EnrollmentTransport transport = new(handler, Policy());
        string secret = Guid.NewGuid().ToString("D");

        // Act
        Response response = await transport.PostAsync<Request, Response>(new("https://first.example/"),
            EnrollmentEndpoint.Reserve, new(secret), TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(1, response.Version);
        Assert.Equal("https://first.example/internal/enrollment/v1/reserve", handler.Address);
        Assert.Equal("POST", handler.Method);
        Assert.Contains(secret, handler.Body);
        Assert.DoesNotContain(secret, handler.Address);
        Assert.DoesNotContain(secret, handler.Headers);
        Assert.Equal(1, handler.Sends);
    }

    [Theory]
    [InlineData(301)]
    [InlineData(302)]
    [InlineData(303)]
    [InlineData(307)]
    [InlineData(308)]
    public async Task RedirectCannotSendInvitationToAnotherOrigin(int status)
    {
        // Arrange
        Handler handler = new() { Status = (HttpStatusCode)status };
        using EnrollmentTransport transport = new(handler, Policy());

        // Act
        Exception? failure = await Record.ExceptionAsync(() => transport.PostAsync<Request, Response>(
            new("https://first.example/"), EnrollmentEndpoint.Reserve, new("private-code"), TestContext.Current.CancellationToken));

        // Assert
        Assert.IsType<InvalidDataException>(failure);
        Assert.Equal("EnrollmentRedirectRejected", failure.Message);
        Assert.Equal(1, handler.Sends);
    }

    [Theory]
    [InlineData("http://first.example/")]
    [InlineData("https://name:secret@first.example/")]
    [InlineData("https://first.example/?code=secret")]
    [InlineData("https://first.example/#secret")]
    [InlineData("https://first.example/internal/raft")]
    [InlineData("https://127.0.0.1/")]
    [InlineData("https://169.254.169.254/")]
    [InlineData("https://100.100.100.200/")]
    [InlineData("https://10.0.0.1/")]
    [InlineData("https://foo.localhost/")]
    [InlineData("https://first.example./")]
    public async Task InvalidOriginIsRejectedBeforeTransport(string origin)
    {
        // Arrange
        Handler handler = new();
        using EnrollmentTransport transport = new(handler, Policy());

        // Act
        Exception? failure = await Record.ExceptionAsync(() => transport.DiscoverAsync<Response>(new(origin), TestContext.Current.CancellationToken));

        // Assert
        Assert.IsType<InvalidDataException>(failure);
        Assert.Equal(0, handler.Sends);
    }

    [Theory]
    [InlineData("large")]
    [InlineData("chunked")]
    [InlineData("encoding")]
    [InlineData("type")]
    [InlineData("duplicate")]
    [InlineData("unknown")]
    public async Task ResponseLimitsAndStrictJsonAreEnforcedBeforeReturningData(string mutation)
    {
        // Arrange
        Handler handler = new() { Mutation = mutation };
        using EnrollmentTransport transport = new(handler, Policy());

        // Act
        Exception? failure = await Record.ExceptionAsync(() => transport.DiscoverAsync<Response>(new("https://first.example/"), TestContext.Current.CancellationToken));

        // Assert
        Assert.IsType<InvalidDataException>(failure);
        Assert.Equal(1, handler.Sends);
    }

    [Fact]
    public async Task OversizedRequestAndUnknownEndpointDoNotReachTransport()
    {
        // Arrange
        Handler handler = new();
        using EnrollmentTransport transport = new(handler, Policy());

        // Act
        Exception? large = await Record.ExceptionAsync(() => transport.PostAsync<Request, Response>(new("https://first.example/"),
            EnrollmentEndpoint.Reserve, new(new string('a', EnrollmentTransport.MAX_BYTES)), TestContext.Current.CancellationToken));
        Exception? unknown = await Record.ExceptionAsync(() => transport.PostAsync<Request, Response>(new("https://first.example/"),
            (EnrollmentEndpoint)100, new("secret"), TestContext.Current.CancellationToken));

        // Assert
        Assert.IsType<InvalidDataException>(large);
        Assert.IsType<InvalidDataException>(unknown);
        Assert.Equal(0, handler.Sends);
    }

    [Fact]
    public void AdvertisedEnrollmentCannotMoveOriginOrReplacePeerProbeWithBrowserAddress()
    {
        // Arrange
        EnrollmentEndpoints policy = Policy();
        Uri origin = new("https://first.example/");

        // Act
        Exception? valid = Record.Exception(() => policy.ValidateAdvertised(origin,
            "https://first.example/internal/enrollment/v1/", "https://peer.example:444/internal/raft"));
        Exception? moved = Record.Exception(() => policy.ValidateAdvertised(origin,
            "https://other.example/internal/enrollment/v1/", "https://peer.example:444/internal/raft"));
        Exception? browser = Record.Exception(() => policy.ValidateAdvertised(origin,
            "https://first.example/internal/enrollment/v1/", "https://first.example/"));

        // Assert
        Assert.Null(valid);
        Assert.IsType<InvalidDataException>(moved);
        Assert.IsType<InvalidDataException>(browser);
    }

    [Fact]
    public void EveryDnsResolutionMustContainOnlyAllowedAddressesAndPinsExactDialAddress()
    {
        // Arrange
        SetupPeerAddressPolicy policy = new(new() { AllowedPeerNetworks = ["10.9.0.0/16", "fd12:3456::/32"] });
        IPAddress allowed = IPAddress.Parse("10.9.0.2");

        // Act
        IPAddress selected = policy.SelectAddress([allowed, IPAddress.Parse("fd12:3456::4")]);
        Exception? rebound = Record.Exception(() => policy.SelectAddress([allowed, IPAddress.Parse("169.254.169.254")]));
        Exception? privateChanged = Record.Exception(() => policy.SelectAddress([IPAddress.Parse("10.8.0.2")]));
        Exception? mapped = Record.Exception(() => policy.SelectAddress([IPAddress.Parse("::ffff:127.0.0.1")]));
        Exception? empty = Record.Exception(() => policy.SelectAddress([]));

        // Assert
        Assert.Same(allowed, selected);
        Assert.IsType<HttpRequestException>(rebound);
        Assert.IsType<HttpRequestException>(privateChanged);
        Assert.IsType<HttpRequestException>(mapped);
        Assert.IsType<HttpRequestException>(empty);
    }

    private static EnrollmentEndpoints Policy()
    {
        return new(new SetupPeerAddressPolicy(new()));
    }

    private record Request(string Invitation)
    {
        public override string ToString()
        {
            return "EnrollmentRequest";
        }
    }

    private record Response(int Version);

    private class Handler : HttpMessageHandler
    {
        public HttpStatusCode Status { get; init; } = HttpStatusCode.OK;
        public string? Mutation { get; init; }
        public string? Address { get; private set; }
        public string? Method { get; private set; }
        public string? Headers { get; private set; }
        public string? Body { get; private set; }
        public int Sends { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Sends++;
            Address = request.RequestUri!.AbsoluteUri;
            Method = request.Method.Method;
            Headers = request.Headers.ToString();
            if (request.Content is not null)
            {
                Body = await request.Content.ReadAsStringAsync(token);
            }

            string data = Mutation switch
            {
                "large" or "chunked" => new string(' ', EnrollmentTransport.MAX_BYTES + 1),
                "duplicate" => "{\"Version\":1,\"Version\":1}",
                "unknown" => "{\"Version\":1,\"Other\":2}",
                _ => "{\"Version\":1}"
            };
            HttpResponseMessage response = new(Status) { Content = new StringContent(data, Encoding.UTF8, "application/json") };
            response.Headers.Location = new("https://attacker.example/steal");
            if (Mutation == "chunked")
            {
                response.Content = new StreamContent(new MemoryStream(Encoding.UTF8.GetBytes(data)));
                response.Content.Headers.ContentType = new("application/json");
            }
            else if (Mutation == "encoding")
            {
                response.Content.Headers.ContentEncoding.Add("gzip");
            }
            else if (Mutation == "type")
            {
                response.Content.Headers.ContentType = new MediaTypeHeaderValue("text/html");
            }

            return response;
        }
    }
}
