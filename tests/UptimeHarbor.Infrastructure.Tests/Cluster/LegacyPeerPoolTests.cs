using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using UptimeHarbor.Infrastructure.Cluster;
using Xunit;
using Scenario = UptimeHarbor.Tests.Infrastructure.Cluster.ManagedPeerPoolTests.Scenario;

namespace UptimeHarbor.Tests.Infrastructure.Cluster;

public class LegacyPeerPoolTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LegacyDirectBindsEitherOverlapLeafAndRechecksOnlineForPooledReuse(bool replacement)
    {
        // Arrange
        using Scenario scenario = new(legacy: true);
        scenario.Offered = replacement ? scenario.Next : scenario.Old;
        await scenario.SendAsync(typed: false);

        // Act
        await scenario.SendAsync();
        await scenario.SendAsync();
        await scenario.SendAsync();

        // Assert
        Assert.Equal(1, scenario.OrdinarySends);
        Assert.Equal(3, scenario.Sends);
        Assert.Equal(2, scenario.Handshakes);
        Assert.Equal(2, scenario.ChainChecks);
        Assert.Equal(1, scenario.PoolCount);
        Assert.NotEmpty(scenario.Confirmed);
        Assert.All(scenario.Confirmed, id => Assert.Equal(scenario.Offered.CredentialId, id));
    }

    [Theory]
    [InlineData("overlap-ended", 2, 1)]
    [InlineData("revoked", 2, 1)]
    [InlineData("crl-unavailable", 2, 1)]
    [InlineData("crl-revoked", 2, 1)]
    [InlineData("different-chain", 2, 1)]
    [InlineData("wrong-local", 2, 0)]
    [InlineData("wrong-server-leaf", 2, 0)]
    [InlineData("wrong-reply-id", 3, 0)]
    public async Task LegacyDirectFailsClosedWithoutRetryingTypedRequest(string change, int sends, int disposed)
    {
        // Arrange
        using Scenario scenario = new(legacy: true);
        scenario.OverlapUntil = scenario.Clock.Now.AddSeconds(1);
        await scenario.SendAsync();
        await scenario.SendAsync();
        switch (change)
        {
            case "overlap-ended":
                scenario.Clock.Now = scenario.OverlapUntil.Value;
                break;
            case "revoked":
                scenario.RetireOld();
                break;
            case "crl-unavailable":
                scenario.ChainStatus = X509ChainStatusFlags.RevocationStatusUnknown;
                break;
            case "crl-revoked":
                scenario.ChainStatus = X509ChainStatusFlags.Revoked;
                break;
            case "different-chain":
                scenario.ChainDigest = "replacement-chain";
                scenario.Reconnect = true;
                break;
            case "wrong-local":
                scenario.WrongLocal = true;
                scenario.Reconnect = true;
                break;
            case "wrong-server-leaf":
                scenario.RetireNext();
                scenario.Offered = scenario.Next;
                scenario.Reconnect = true;
                break;
            case "wrong-reply-id":
                scenario.ReplyId = scenario.Next.CredentialId;
                break;
        }

        // Act
        await Assert.ThrowsAsync<HttpRequestException>(() => scenario.SendAsync());

        // Assert
        Assert.Equal(sends, scenario.Sends);
        Assert.Equal(disposed, scenario.DisposedPools);
        Assert.Equal(1, scenario.PoolCount);
        Assert.Equal(0, scenario.OrdinarySends);
    }

    [Fact]
    public async Task LegacyDirectMissingChainProofCannotStartHttp()
    {
        // Arrange
        using Scenario scenario = new(legacy: true) { ChainDigest = null };

        // Act
        await Assert.ThrowsAsync<HttpRequestException>(() => scenario.SendAsync());

        // Assert
        Assert.Equal(0, scenario.Sends);
        Assert.Equal(1, scenario.Handshakes);
    }

    [Fact]
    public void LegacyDirectKeepsOrdinarySocketOptionsAndRejectsPlatformTlsErrors()
    {
        // Arrange
        using ECDsa key = ECDsa.Create();
        using X509Certificate2 leaf = new CertificateRequest("CN=peer.example.test", key, HashAlgorithmName.SHA256)
            .CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
        using SocketsHttpHandler ordinary = LegacyPeerTransport.CreateSocketsHandler(leaf);
        using SocketsHttpHandler fresh = LegacyPeerTransport.CreateBoundHandler(leaf, false,
            (_, _, _, _, _) => throw new InvalidOperationException());
        using SocketsHttpHandler pooled = LegacyPeerTransport.CreateBoundHandler(leaf, true,
            (_, _, _, _, _) => throw new InvalidOperationException());
        using MemoryStream bytes = new();
        using SslStream stream = new(bytes);

        // Act
        RemoteCertificateValidationCallback validate = Assert.IsType<RemoteCertificateValidationCallback>(
            pooled.SslOptions.RemoteCertificateValidationCallback);

        // Assert
        foreach (SocketsHttpHandler handler in new[] { ordinary, fresh, pooled })
        {
            Assert.False(handler.AllowAutoRedirect);
            Assert.False(handler.UseCookies);
            Assert.False(handler.UseProxy);
            Assert.Equal(TimeSpan.FromSeconds(3), handler.ConnectTimeout);
            Assert.Same(leaf, Assert.Single(handler.SslOptions.ClientCertificates!));
        }

        Assert.Equal(TimeSpan.FromMinutes(1), ordinary.PooledConnectionLifetime);
        Assert.Equal(X509RevocationMode.Online, ordinary.SslOptions.CertificateRevocationCheckMode);
        Assert.Null(ordinary.PlaintextStreamFilter);
        Assert.Equal(TimeSpan.Zero, fresh.PooledConnectionLifetime);
        Assert.Equal(ordinary.PooledConnectionLifetime, pooled.PooledConnectionLifetime);
        Assert.NotNull(pooled.PlaintextStreamFilter);
        Assert.False(pooled.SslOptions.AllowTlsResume);
        Assert.False(pooled.SslOptions.AllowRenegotiation);
        X509ChainPolicy policy = Assert.IsType<X509ChainPolicy>(pooled.SslOptions.CertificateChainPolicy);
        Assert.Equal(X509ChainTrustMode.System, policy.TrustMode);
        Assert.Equal(X509RevocationMode.NoCheck, policy.RevocationMode);
        Assert.True(policy.DisableCertificateDownloads);
        Assert.Equal(X509VerificationFlags.NoFlag, policy.VerificationFlags);
        Assert.Equal("1.3.6.1.5.5.7.3.1", Assert.Single(policy.ApplicationPolicy.Cast<Oid>()).Value);
        Assert.False(validate(stream, leaf, null, SslPolicyErrors.RemoteCertificateNameMismatch));
        Assert.False(validate(stream, leaf, null, SslPolicyErrors.RemoteCertificateChainErrors));
        Assert.False(validate(stream, leaf, null, SslPolicyErrors.None));
    }
}
