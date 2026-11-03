using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Cluster.Onboarding;
using UptimeHarbor.Infrastructure.Cluster.Onboarding;
using Xunit;

namespace UptimeHarbor.Tests.Infrastructure.Installation;

public class EnrollmentSigningJournalTests
{
    [Fact]
    public async Task LostResponseReturnsOriginalDerForSameIssueAndDoesNotCommitAgain()
    {
        // Arrange
        using EnrollmentCertificateFixture fixture = new();
        MemoryWork work = Work(fixture);
        EnrollmentSigningJournal journal = new(work, new());
        using X509Certificate2 first = fixture.Issue();
        using X509Certificate2 retried = fixture.Issue();
        EnrollmentIssuedArtifact original = Artifact(fixture, first);

        // Act
        EnrollmentIssuedArtifact saved = await journal.SaveIssueOnceAsync(original, fixture.Request, fixture.Template, fixture.Now, TestContext.Current.CancellationToken);
        EnrollmentIssuedArtifact replay = await journal.SaveIssueOnceAsync(Artifact(fixture, retried), fixture.Request, fixture.Template, fixture.Now, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(first.RawData, saved.CertificateDer);
        Assert.Equal(first.RawData, replay.CertificateDer);
        Assert.Equal(original.CertificateDigest, replay.CertificateDigest);
        Assert.Equal(1, work.Commits);
        Assert.Equal(original.IssueId, replay.IssueId);
    }

    [Fact]
    public async Task FailedCommitDoesNotReturnDerOrPublishResult()
    {
        // Arrange
        using EnrollmentCertificateFixture fixture = new();
        MemoryWork work = Work(fixture);
        work.FailCommit = true;
        EnrollmentSigningJournal journal = new(work, new());
        using X509Certificate2 leaf = fixture.Issue();

        // Act
        Exception? failure = await Record.ExceptionAsync(() => journal.SaveIssueOnceAsync(Artifact(fixture, leaf),
            fixture.Request, fixture.Template, fixture.Now, TestContext.Current.CancellationToken));

        // Assert
        Assert.IsType<IOException>(failure);
        Assert.Empty(work.Issued);
        Assert.Equal(0, work.Commits);
    }

    [Theory]
    [InlineData("attempt")]
    [InlineData("digest")]
    [InlineData("template")]
    [InlineData("authorization")]
    [InlineData("der")]
    public async Task ReusedIssueCannotChangeDurableBindingOrAdoptCorruptDer(string mutation)
    {
        // Arrange
        using EnrollmentCertificateFixture fixture = new();
        MemoryWork work = Work(fixture);
        using X509Certificate2 leaf = fixture.Issue();
        EnrollmentIssuedArtifact saved = Artifact(fixture, leaf);
        work.Issued.Add(saved.IssueId, saved);
        EnrollmentIssuedArtifact changed = mutation switch
        {
            "attempt" => saved with { AttemptId = Guid.NewGuid() },
            "digest" => saved with { RequestDigest = new string('F', 64) },
            "template" => saved with { TemplateDigest = new string('F', 64) },
            "authorization" => saved with { AuthorizationIndex = 9 },
            "der" => saved with { CertificateDer = [0, 1, 2] },
            _ => throw new ArgumentOutOfRangeException(nameof(mutation))
        };
        EnrollmentSigningJournal journal = new(work, new());

        // Act
        Exception? failure = await Record.ExceptionAsync(() => journal.SaveIssueOnceAsync(changed,
            fixture.Request, fixture.Template, fixture.Now, TestContext.Current.CancellationToken));

        // Assert
        Assert.IsType<InvalidDataException>(failure);
        Assert.Equal(0, work.Commits);
        Assert.Same(saved, Assert.Single(work.Issued).Value);
    }

    [Fact]
    public async Task IssuerReferenceMustMatchProtectedLocalKeyAndExactInstallation()
    {
        // Arrange
        using EnrollmentCertificateFixture fixture = new();
        using InstallationFixture installation = new();
        byte[] pem = Encoding.UTF8.GetBytes(fixture.IssuerKey.ExportPkcs8PrivateKeyPem());
        string reference;
        try
        {
            reference = installation.Secrets.Write(fixture.Epoch, "issuer", pem);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pem);
        }

        EnrollmentIssuerReference issuer = new(fixture.Epoch, installation.Lease.InstallIdentity, fixture.Request.ClusterId,
            1, fixture.Request.NodeId, fixture.Request.StorageGeneration, reference, fixture.Issuer.RawData,
            EnrollmentCertificates.Hash(fixture.Issuer.RawData), EnrollmentCertificates.Hash(fixture.IssuerKey.ExportSubjectPublicKeyInfo()));
        MemoryWork work = new();
        EnrollmentSigningJournal journal = new(work, new());

        // Act
        EnrollmentIssuerReference saved = await journal.SaveIssuerOnceAsync(issuer, installation.Secrets, installation.Lease,
            fixture.Now, TestContext.Current.CancellationToken);
        Exception? foreign = await Record.ExceptionAsync(() => journal.SaveIssuerOnceAsync(issuer with { InstallIdentity = Guid.NewGuid() },
            installation.Secrets, installation.Lease, fixture.Now, TestContext.Current.CancellationToken));
        Exception? orphan = Record.Exception(() => installation.Secrets.ReconcileAll(null));

        // Assert
        Assert.Equal(issuer.CertificateDigest, saved.CertificateDigest);
        Assert.Equal(1, work.Commits);
        Assert.IsType<InvalidDataException>(foreign);
        Assert.IsType<InvalidDataException>(orphan);
    }

    private static MemoryWork Work(EnrollmentCertificateFixture fixture)
    {
        MemoryWork work = new();
        work.Issuers.Add(fixture.Epoch, new(fixture.Epoch, Guid.NewGuid(), fixture.Request.ClusterId,
            fixture.Request.Generation, Guid.NewGuid(), Guid.NewGuid(), "local-ref", fixture.Issuer.RawData,
            EnrollmentCertificates.Hash(fixture.Issuer.RawData), EnrollmentCertificates.Hash(fixture.IssuerKey.ExportSubjectPublicKeyInfo())));
        return work;
    }

    private static EnrollmentIssuedArtifact Artifact(EnrollmentCertificateFixture fixture, X509Certificate2 leaf)
    {
        return new(fixture.Template.IssueId, fixture.Epoch, fixture.Request.AttemptId,
            EnrollmentRequestCodec.Digest(fixture.Request), EnrollmentSigningJournal.TemplateDigest(fixture.Template), null,
            7, leaf.RawData, EnrollmentCertificates.Hash(leaf.RawData));
    }

    internal class MemoryWork : IEnrollmentSigningRepository, IEnrollmentSigningUnitOfWork
    {
        public Task<IReadOnlyList<EnrollmentIssuerReference>> ListIssuersAsync(CancellationToken token)
        {
            return Task.FromResult<IReadOnlyList<EnrollmentIssuerReference>>(Issuers.Values.ToArray());
        }

        public Dictionary<Guid, EnrollmentIssuerReference> Issuers { get; } = [];
        public Dictionary<Guid, EnrollmentIssuedArtifact> Issued { get; } = [];
        private EnrollmentIssuerReference? pendingIssuer;
        private EnrollmentIssuedArtifact? pendingIssue;
        public int Commits { get; private set; }
        public bool FailCommit { get; set; }
        public IEnrollmentSigningRepository Signing => this;

        public Task<EnrollmentIssuerReference?> FindIssuerAsync(Guid epoch, CancellationToken token)
        {
            return Task.FromResult(Issuers.GetValueOrDefault(epoch));
        }

        public Task<EnrollmentIssuedArtifact?> FindIssueAsync(Guid id, CancellationToken token)
        {
            return Task.FromResult(Issued.GetValueOrDefault(id));
        }

        public void AddIssuer(EnrollmentIssuerReference issuer)
        {
            pendingIssuer = issuer;
        }

        public void AddIssue(EnrollmentIssuedArtifact artifact)
        {
            pendingIssue = artifact;
        }

        public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            if (FailCommit)
            {
                pendingIssue = null;
                pendingIssuer = null;
                throw new IOException("SimulatedCommitFailure");
            }

            if (pendingIssuer is not null)
            {
                Issuers.Add(pendingIssuer.Epoch, pendingIssuer);
                pendingIssuer = null;
            }

            if (pendingIssue is not null)
            {
                Issued.Add(pendingIssue.IssueId, pendingIssue);
                pendingIssue = null;
            }

            Commits++;
            return Task.FromResult(1);
        }

        public async Task ExecuteInTransactionAsync(Func<CancellationToken, Task> action, CancellationToken cancellationToken = default)
        {
            await action(cancellationToken);
            await SaveChangesAsync(cancellationToken);
        }
    }
}
