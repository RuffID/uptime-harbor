using UptimeHarbor.Infrastructure.Cluster;
using UptimeHarbor.Web.Cluster;
using Xunit;

namespace UptimeHarbor.Tests.Web.Cluster;

public class ListenerConfigurationTests
{
    [Theory]
    [InlineData("Direct", "https://127.0.0.1:8443", "http://127.0.0.1:5000", true)]
    [InlineData("NginxIsolatedHttp", "http://127.0.0.1:8443", "http://127.0.0.1:5000", true)]
    [InlineData("Direct", "http://127.0.0.1:8443", "http://127.0.0.1:5000", false)]
    [InlineData("Direct", "https://127.0.0.1:8443", "https://127.0.0.1:8443", false)]
    [InlineData("Direct", "https://127.0.0.1:8444", "http://127.0.0.1:5000", false)]
    [InlineData("Direct", null, "http://127.0.0.1:5000", false)]
    public void PeerBindingMustMatchExplicitTlsBoundaryAndSeparatePort(string boundary, string? peer, string browser, bool valid)
    {
        // Arrange
        ClusterOptions options = new() { TlsBoundary = boundary, PeerListenPort = 8443 };

        // Act
        Exception? error = Record.Exception(() => ClusterListenerConfiguration.ValidateEndpoint(options, peer, browser));

        // Assert
        Assert.Equal(valid, error is null);
    }
}
