using System.Security.Cryptography;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Cluster.Onboarding;
using UptimeHarbor.Application.Cluster.Onboarding.Renewal;
using UptimeHarbor.Domain.Cluster;
using UptimeHarbor.Infrastructure.Cluster.Onboarding;
using UptimeHarbor.Infrastructure.Installation.Renewal.Models;
using Xunit;

namespace UptimeHarbor.Tests.Infrastructure.Installation.Renewal;

public class LocalVoterCredentialValidationTests
{
    [Theory]
    [InlineData("version")]
    [InlineData("install")]
    [InlineData("reference")]
    [InlineData("cluster")]
    [InlineData("storage")]
    [InlineData("revision")]
    [InlineData("csr")]
    [InlineData("key")]
    [InlineData("previous-key")]
    public async Task Reopen_RejectsMismatchedProtectedIntentOrPrivateKey(string field)
    {
        // Arrange
        using LocalVoterCredentialFixture fixture = new();
        await fixture.AuthorizeAsync();
        fixture.RewriteIntent(intent => MutateIntent(intent, field), field == "key");
        fixture.Reopen();

        // Act
        Exception? error = await Record.ExceptionAsync(() => fixture.Store.ReadAsync(fixture.Reference, fixture.Token));

        // Assert
        Assert.NotNull(error);
        Assert.Empty(fixture.Authority.Journal.Issued);
        Assert.False(File.Exists(fixture.PathFor("artifact")));
    }

    [Theory]
    [InlineData("intent", "missing")]
    [InlineData("intent", "corrupt")]
    [InlineData("intent", "truncated")]
    [InlineData("authorization", "missing")]
    [InlineData("authorization", "corrupt")]
    [InlineData("authorization", "truncated")]
    [InlineData("artifact", "corrupt")]
    [InlineData("artifact", "truncated")]
    [InlineData("artifact", "stage-swap")]
    public async Task Reopen_RejectsBrokenFilesWithoutReplacingMaterial(string stage, string damage)
    {
        // Arrange
        using LocalVoterCredentialFixture fixture = new();
        await fixture.AuthorizeAsync();
        await fixture.SignAsync();
        await fixture.PinAsync();
        string path = fixture.PathFor(stage);
        if (damage == "missing")
        {
            File.Delete(path);
        }
        else if (damage == "truncated")
        {
            File.WriteAllText(path, "short");
        }
        else if (damage == "stage-swap")
        {
            File.WriteAllBytes(path, File.ReadAllBytes(fixture.PathFor("authorization")));
        }
        else
        {
            byte[] bytes = File.ReadAllBytes(path);
            bytes[50] = (byte)'A';
            bytes[51] = (byte)'B';
            File.WriteAllBytes(path, bytes);
        }

        fixture.Reopen();

        // Act
        Exception? error = await Record.ExceptionAsync(() => fixture.Store.ReadAsync(fixture.Reference, fixture.Token));

        // Assert
        Assert.NotNull(error);
        Assert.Equal(1, fixture.Authority.Journal.Commits);
        Assert.Equal(2, fixture.Authority.Scenario.State.Index);
        Assert.Equal(3, fixture.Logs.Messages.Count);
    }

    [Theory]
    [InlineData("wrapping-missing")]
    [InlineData("wrapping-corrupt")]
    [InlineData("previous-missing")]
    [InlineData("previous-corrupt")]
    [InlineData("orphan")]
    public async Task MissingSecretOrOrphan_FailsClosedAndKeepsOriginalIntent(string damage)
    {
        // Arrange
        using LocalVoterCredentialFixture fixture = new();
        await fixture.PrepareAsync();
        byte[] expected = File.ReadAllBytes(fixture.PathFor("intent"));
        string wrapping = Path.Combine(fixture.Authority.Installation.Options.SecretDirectory, "wrapping.key");
        string previous = Path.Combine(fixture.Authority.Installation.Options.KeyDirectory,
            fixture.PreviousKeyReference.Split(':')[0] + ".key");
        switch (damage)
        {
            case "wrapping-missing":
                File.Delete(wrapping);
                break;
            case "wrapping-corrupt":
                File.WriteAllBytes(wrapping, RandomNumberGenerator.GetBytes(32));
                break;
            case "previous-missing":
                File.Delete(previous);
                break;
            case "previous-corrupt":
                File.WriteAllBytes(previous, RandomNumberGenerator.GetBytes(64));
                break;
            default:
                File.WriteAllText(fixture.PathFor("orphan.tmp"), "incomplete");
                break;
        }

        fixture.Reopen();

        // Act
        Exception? error = await Record.ExceptionAsync(() => fixture.PrepareAsync());

        // Assert
        Assert.NotNull(error);
        Assert.Equal(expected, File.ReadAllBytes(fixture.PathFor("intent")));
        Assert.Empty(fixture.Authority.Journal.Issued);
        if (damage == "wrapping-missing")
        {
            Assert.False(File.Exists(wrapping));
        }
    }

    [Theory]
    [InlineData("index")]
    [InlineData("operation")]
    [InlineData("csr")]
    [InlineData("process")]
    [InlineData("revision")]
    [InlineData("decision")]
    public async Task PinAuthorization_RequiresExactLocalIntentAndAcceptedAuthority(string field)
    {
        // Arrange
        using LocalVoterCredentialFixture fixture = new();
        await fixture.PrepareAsync();
        await fixture.Authority.AuthorizeAsync();
        VoterCredentialSigningRequest call = fixture.Authority.SigningCall();
        RevisionImage image = fixture.Authority.Scenario.State;
        switch (field)
        {
            case "index":
                call = call with { AuthorizationIndex = call.AuthorizationIndex + 1 };
                break;
            case "operation":
                call = call with { ProofRequest = call.ProofRequest with
                {
                    Request = call.ProofRequest.Request with { OperationId = Guid.NewGuid() }
                } };
                break;
            case "csr":
                call = call with { ProofRequest = call.ProofRequest with { CsrDer = Convert.ToBase64String(new byte[128]) } };
                break;
            case "process":
                call = call with { ProofRequest = call.ProofRequest with
                {
                    Proof = call.ProofRequest.Proof with { ProcessEpoch = call.ProofRequest.Proof.ProcessEpoch + 1 }
                } };
                break;
            case "revision":
                VoterCredentialRotation rotation = image.OnboardingContracts!.VoterCredentialRotations![fixture.Authority.Request.Renewal.Issuance.IssueId];
                image = image with { OnboardingContracts = image.OnboardingContracts with
                {
                    VoterCredentialRotations = image.OnboardingContracts.VoterCredentialRotations.SetItem(rotation.Renewal.Issuance.IssueId,
                        rotation with { Renewal = rotation.Renewal with { PreviousCredentialRevision = 44 } })
                } };
                break;
            default:
                image = image with { Results = image.Results.Remove(fixture.Authority.Request.OperationId) };
                break;
        }

        fixture.Authority.Subject.Publish(image);

        // Act
        Exception? error = await Record.ExceptionAsync(() => fixture.Store.PinAuthorizationAsync(fixture.Reference,
            call, fixture.Token));

        // Assert
        Assert.NotNull(error);
        Assert.False(File.Exists(fixture.PathFor("authorization")));
        Assert.Single(fixture.Logs.Messages);
        Assert.Empty(fixture.Authority.Journal.Issued);
    }

    [Theory]
    [InlineData("issue")]
    [InlineData("epoch")]
    [InlineData("operation")]
    [InlineData("index")]
    [InlineData("fingerprint")]
    [InlineData("der")]
    [InlineData("template")]
    [InlineData("digest")]
    public async Task PinArtifact_RejectsAlteredSignerResponseBeforeDurableWrite(string field)
    {
        // Arrange
        using LocalVoterCredentialFixture fixture = new();
        await fixture.AuthorizeAsync();
        EnrollmentIssuedArtifact artifact = await fixture.SignAsync();
        EnrollmentIssuedArtifact changed = MutateArtifact(artifact, field);

        // Act
        Exception? error = await Record.ExceptionAsync(() => fixture.Store.PinArtifactAsync(fixture.Reference,
            fixture.Authority.SigningCall(), changed, fixture.Token));

        // Assert
        Assert.NotNull(error);
        Assert.False(File.Exists(fixture.PathFor("artifact")));
        Assert.Equal(2, fixture.Logs.Messages.Count);
        Assert.Equal(1, fixture.Authority.Journal.Commits);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AfterAcceptedBindOrRetirement_ReadUsesPinsAndCannotSignOrRepin(bool retired)
    {
        // Arrange
        using LocalVoterCredentialFixture fixture = new();
        await fixture.AuthorizeAsync();
        EnrollmentIssuedArtifact artifact = await fixture.SignAsync();
        LocalVoterCredentialState pinned = await fixture.PinAsync();
        RevisionCommand basis = fixture.Authority.Scenario.Command("BindVoterCredential");
        OperationalCredential credential = new(artifact.IssueId, fixture.Authority.SubjectId, artifact.CertificateDigest,
            Convert.ToBase64String(artifact.CertificateDer), pinned.Intent.Request.Renewal.Issuance.NotAfterUtc, null, null);
        RevisionCommand bind = basis with
        {
            BaseRevision = pinned.Intent.Request.Renewal.PreviousCredentialRevision,
            EnrollmentTrust = basis.EnrollmentTrust! with { Rotation = new(pinned.Intent.Request.Renewal, credential, null) }
        };
        Assert.Equal(OperationOutcome.Committed, fixture.Authority.Scenario.Apply(bind).Outcome);
        if (retired)
        {
            RevisionCommand retire = fixture.Authority.Scenario.Command("RetireOldCredential");
            retire = retire with
            {
                BaseRevision = pinned.Intent.Request.Renewal.PreviousCredentialRevision,
                EnrollmentTrust = retire.EnrollmentTrust! with
                {
                    Rotation = new(pinned.Intent.Request.Renewal, null, new string('E', 64))
                }
            };
            Assert.Equal(OperationOutcome.Committed, fixture.Authority.Scenario.Apply(retire).Outcome);
        }

        fixture.Authority.Publish();
        fixture.Reopen();
        int calls = fixture.Authority.Remote.Calls;

        // Act
        LocalVoterCredentialState read = await fixture.Store.ReadAsync(fixture.Reference, fixture.Token);
        Exception? signing = await Record.ExceptionAsync(() => fixture.SignAsync());
        Exception? repin = await Record.ExceptionAsync(() => fixture.PinAsync());

        // Assert
        Assert.Equal(pinned.Intent, read.Intent);
        Assert.Equal(artifact.CertificateDer, read.Artifact!.CertificateDer);
        Assert.Equal(pinned.Authorization, read.Authorization);
        Assert.NotNull(signing);
        Assert.NotNull(repin);
        Assert.Equal(calls, fixture.Authority.Remote.Calls);
        Assert.Equal(1, fixture.Authority.Journal.Commits);
        Assert.Equal(3, fixture.Logs.Messages.Count);
    }

    private static LocalVoterCredentialIntent MutateIntent(LocalVoterCredentialIntent intent, string field)
    {
        switch (field)
        {
            case "version":
                return intent with { Version = 99 };
            case "install":
                return intent with { InstallIdentity = Guid.NewGuid() };
            case "reference":
                return intent with { KeyReference = new string('A', 64) };
            case "cluster":
                return intent with { Request = intent.Request with { ClusterId = Guid.NewGuid() } };
            case "storage":
                return intent with { Request = intent.Request with { Renewal = intent.Request.Renewal with
                {
                    Issuance = intent.Request.Renewal.Issuance with { StorageGeneration = Guid.NewGuid() }
                } } };
            case "revision":
                return intent with { Request = intent.Request with { Renewal = intent.Request.Renewal with { PreviousCredentialRevision = 42 } } };
            case "csr":
                return intent with { CsrDer = Convert.ToBase64String(new byte[128]) };
            case "previous-key":
                return intent with { PreviousKeyOwner = Guid.NewGuid() };
            default:
                return intent;
        }
    }

    private static EnrollmentIssuedArtifact MutateArtifact(EnrollmentIssuedArtifact artifact, string field)
    {
        switch (field)
        {
            case "issue":
                return artifact with { IssueId = Guid.NewGuid() };
            case "epoch":
                return artifact with { IssuerEpoch = Guid.NewGuid() };
            case "operation":
                return artifact with { AttemptId = Guid.NewGuid() };
            case "index":
                return artifact with { AuthorizationIndex = artifact.AuthorizationIndex + 1 };
            case "fingerprint":
                return artifact with { CertificateDigest = new string('F', 64) };
            case "der":
                return artifact with { CertificateDer = new byte[128] };
            case "template":
                return artifact with { TemplateDigest = new string('F', 64) };
            default:
                return artifact with { RequestDigest = new string('F', 64) };
        }
    }
}
