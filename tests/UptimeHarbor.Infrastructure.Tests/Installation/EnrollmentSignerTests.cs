using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Cluster.Onboarding;
using UptimeHarbor.Infrastructure.Cluster.Onboarding;
using Xunit;

namespace UptimeHarbor.Tests.Infrastructure.Installation;

public class EnrollmentSignerTests
{
    [Fact]
    public async Task RetryChecksFreshAuthorityAndReturnsDurableOriginalDerWithoutSecondSuccessLog()
    {
        // Arrange
        using EnrollmentCertificateFixture crypto = new();
        using InstallationFixture installation = new();
        EnrollmentSigningJournalTests.MemoryWork work = Work(crypto, installation);
        Authorization authorization = new();
        CapturedLog logger = new();
        EnrollmentSigner signer = Signer(crypto, installation, work, authorization, logger);

        // Act
        EnrollmentIssuedArtifact first = await signer.IssueAsync(crypto.Request, crypto.Template, crypto.Csr, TestContext.Current.CancellationToken);
        EnrollmentIssuedArtifact retry = await signer.IssueAsync(crypto.Request, crypto.Template, crypto.Csr, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(first.IssueId, retry.IssueId);
        Assert.Equal(first.CertificateDer, retry.CertificateDer);
        Assert.Equal(1, work.Commits);
        Assert.Equal(2, authorization.Calls);
        Assert.Single(logger.Messages);
        Assert.DoesNotContain("PRIVATE KEY", Assert.Single(logger.Messages));
        Assert.DoesNotContain(Convert.ToBase64String(first.CertificateDer), Assert.Single(logger.Messages));
    }

    [Theory]
    [InlineData("authority")]
    [InlineData("commit")]
    [InlineData("csr")]
    [InlineData("key")]
    public async Task RejectionOrUnknownCommitCannotReturnCertificateOrLogSuccess(string mutation)
    {
        // Arrange
        using EnrollmentCertificateFixture crypto = new();
        using EnrollmentCertificateFixture foreign = new();
        using InstallationFixture installation = new();
        EnrollmentSigningJournalTests.MemoryWork work = Work(crypto, installation);
        Authorization authorization = new() { Allowed = mutation != "authority" };
        CapturedLog logger = new();
        EnrollmentSigner signer = Signer(crypto, installation, work, authorization, logger);
        byte[] csr = crypto.Csr;
        if (mutation == "commit")
        {
            work.FailCommit = true;
        }
        else if (mutation == "csr")
        {
            csr = foreign.Csr;
        }
        else if (mutation == "key")
        {
            EnrollmentIssuerReference issuer = work.Issuers[crypto.Epoch];
            work.Issuers[crypto.Epoch] = issuer with { SpkiDigest = new string('F', 64) };
        }

        // Act
        Exception? failure = await Record.ExceptionAsync(() => signer.IssueAsync(crypto.Request, crypto.Template, csr, TestContext.Current.CancellationToken));

        // Assert
        Assert.NotNull(failure);
        Assert.Empty(work.Issued);
        Assert.Empty(logger.Messages);
        Assert.Equal(0, work.Commits);
        if (mutation == "csr")
        {
            Assert.Equal(0, authorization.Calls);
        }
    }

    [Fact]
    public async Task LostResponseDoesNotTurnStoredCertificateIntoAuthorityAfterRevocation()
    {
        // Arrange
        using EnrollmentCertificateFixture crypto = new();
        using InstallationFixture installation = new();
        EnrollmentSigningJournalTests.MemoryWork work = Work(crypto, installation);
        Authorization authorization = new();
        CapturedLog logger = new();
        EnrollmentSigner signer = Signer(crypto, installation, work, authorization, logger);
        EnrollmentIssuedArtifact issued = await signer.IssueAsync(crypto.Request, crypto.Template, crypto.Csr, TestContext.Current.CancellationToken);
        authorization.Allowed = false;

        // Act
        Exception? revoked = await Record.ExceptionAsync(() => signer.IssueAsync(crypto.Request, crypto.Template, crypto.Csr, TestContext.Current.CancellationToken));

        // Assert
        Assert.IsType<UnauthorizedAccessException>(revoked);
        Assert.Equal(issued.CertificateDer, Assert.Single(work.Issued).Value.CertificateDer);
        Assert.Equal(1, work.Commits);
        Assert.Single(logger.Messages);
    }

    private static EnrollmentSigningJournalTests.MemoryWork Work(EnrollmentCertificateFixture crypto, InstallationFixture installation)
    {
        byte[] pem = Encoding.UTF8.GetBytes(crypto.IssuerKey.ExportPkcs8PrivateKeyPem());
        string key;
        try
        {
            key = installation.Secrets.Write(crypto.Epoch, "issuer", pem);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pem);
        }

        EnrollmentSigningJournalTests.MemoryWork work = new();
        work.Issuers.Add(crypto.Epoch, new(crypto.Epoch, installation.Lease.InstallIdentity, crypto.Request.ClusterId,
            crypto.Request.Generation, crypto.Request.NodeId, crypto.Request.StorageGeneration, key, crypto.Issuer.RawData,
            EnrollmentCertificates.Hash(crypto.Issuer.RawData), EnrollmentCertificates.Hash(crypto.IssuerKey.ExportSubjectPublicKeyInfo())));
        return work;
    }

    private static EnrollmentSigner Signer(EnrollmentCertificateFixture crypto, InstallationFixture installation,
        EnrollmentSigningJournalTests.MemoryWork work, Authorization authorization, CapturedLog logger)
    {
        return new(work, new(work, new()), authorization, new(), installation.Lease, installation.Secrets, new FixedTime(crypto.Now), logger);
    }

    private class Authorization : IEnrollmentSigningAuthorization
    {
        public bool Allowed { get; set; } = true;
        public int Calls { get; private set; }

        public ValueTask<long> ConfirmAsync(EnrollmentRequest request, EnrollmentIssuance issuance,
            EnrollmentIssuerReference issuer, CancellationToken token)
        {
            Calls++;
            if (!Allowed)
            {
                throw new UnauthorizedAccessException("SimulatedRevokedAuthority");
            }

            return ValueTask.FromResult(7L);
        }
    }

    private class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow()
        {
            return now;
        }
    }

    private class CapturedLog : ILogger<EnrollmentSigner>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull
        {
            return null;
        }

        public bool IsEnabled(LogLevel level)
        {
            return true;
        }

        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Messages.Add(formatter(state, exception));
        }
    }
}
