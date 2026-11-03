using Microsoft.Extensions.Options;
using UptimeHarbor.Application.Common;
using UptimeHarbor.Infrastructure.Cluster;
using UptimeHarbor.Infrastructure.Installation;
using UptimeHarbor.Infrastructure.Telegram;
using Xunit;

namespace UptimeHarbor.Tests.Infrastructure.Installation;

public class SetupBotIdentityTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OptionalGetMeReadsOnlyPublicIdentityAndSanitizesFailure(bool valid)
    {
        // Arrange
        using Client clients = new(valid);
        SetupBotIdentity identity = new(Options.Create(new ClusterOptions()),
            Options.Create(new TelegramOptions { BotToken = "321:" + new string('A', 40) }), clients);

        // Act
        ServiceResult<long> result = await identity.ReadAsync(CancellationToken.None);

        // Assert
        Assert.Equal(valid, result.Success);
        Assert.Equal(1, clients.Calls);
        if (valid)
        {
            Assert.Equal(321, result.Data);
        }
        else
        {
            Assert.DoesNotContain(new string('A', 40), result.Error!.Message);
            Assert.DoesNotContain("private response", result.Error.Message);
        }
    }

    private class Client(bool valid) : HttpMessageHandler, IHttpClientFactory
    {
        public int Calls { get; private set; }
        public HttpClient CreateClient(string name)
        {
            Assert.Equal(TelegramHealthProbe.CLIENT_NAME, name);
            return new(this, disposeHandler: false) { BaseAddress = new("https://telegram.invalid/") };
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls++;
            if (!valid)
            {
                throw new HttpRequestException("private response with token must be hidden");
            }

            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("{\"ok\":true,\"result\":{\"id\":321}}")
            });
        }
    }
}
