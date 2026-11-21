using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Cluster.Onboarding.Renewal;
using UptimeHarbor.Infrastructure.Cluster.Onboarding.Renewal;
using Xunit;

namespace UptimeHarbor.Tests.Infrastructure.Cluster.Renewal;

public class VoterCredentialBindingAuthorityTests
{
    [Theory]
    [InlineData("command-time", "VoterCredentialBindCommandChanged")]
    [InlineData("coordinator-stamp", "VoterCredentialBindCommandChanged")]
    [InlineData("source", "VoterCredentialBindCommandChanged")]
    [InlineData("substituted-leaf", "VoterCredentialBindCommandChanged")]
    [InlineData("artifact-index", "VoterCredentialSignerResponseInvalid")]
    [InlineData("authority-index", "VoterCredentialExactAuthorizationRequired")]
    [InlineData("local-storage", "EnrollmentPeerVoterRequired")]
    [InlineData("old-expired", "VoterCredentialNotCurrent")]
    [InlineData("old-revoked", "VoterCredentialNotCurrent")]
    [InlineData("old-retired", "VoterCredentialNotCurrent")]
    [InlineData("recovery", "VoterCredentialFreshProjectionRequired")]
    [InlineData("proxy", "VoterCredentialBindingDirectRequired")]
    public async Task ExactBindRejectsSubstitutedOrStaleEvidence(string mutation, string reason)
    {
        using VoterCredentialAuthorityFixture fixture = new();
        CancellationToken token = TestContext.Current.CancellationToken;
        await fixture.AuthorizeAsync();
        VoterCredentialSigningRequest signing = fixture.SigningCall();
        EnrollmentIssuedArtifact artifact = await fixture.Router().IssueAsync(signing, token);
        RevisionCommand command = VoterCredentialBindCommand.Create(fixture.Request, artifact, Guid.NewGuid());
        RevisionImage accepted = fixture.Scenario.State;
        Assert.Same(accepted, await fixture.SubjectAuthority.ConfirmBindingAsync(command, signing, artifact, token));

        OperationalCredential previous = accepted.Operations.Credentials[fixture.Request.Renewal.PreviousCredentialId];
        switch (mutation)
        {
            case "command-time":
                command = command with { CreatedAtUtc = command.CreatedAtUtc.AddSeconds(1) };
                break;
            case "coordinator-stamp":
                command = command with { EnrollmentTrust = command.EnrollmentTrust! with
                {
                    CoordinatorAtUtc = fixture.Time.Now, CoordinatorTerm = accepted.Term
                } };
                break;
            case "source":
                command = command with { OriginNodeId = Guid.NewGuid() };
                break;
            case "substituted-leaf":
                command = command with { EnrollmentTrust = command.EnrollmentTrust! with
                {
                    Rotation = command.EnrollmentTrust!.Rotation! with
                    {
                        Credential = command.EnrollmentTrust!.Rotation!.Credential! with
                        {
                            Fingerprint = previous.Fingerprint, PublicCertificate = previous.PublicCertificate
                        }
                    }
                } };
                break;
            case "artifact-index":
                artifact = artifact with { AuthorizationIndex = artifact.AuthorizationIndex + 1 };
                break;
            case "authority-index":
                signing = signing with { AuthorizationIndex = signing.AuthorizationIndex + 1 };
                break;
            case "local-storage":
                fixture.SubjectOptions.StorageGeneration = Guid.NewGuid();
                break;
            case "old-expired":
                fixture.Time.Now = previous.ExpiresAtUtc;
                break;
            case "old-revoked":
                fixture.Subject.Publish(accepted with { Operations = accepted.Operations with
                {
                    Credentials = accepted.Operations.Credentials.SetItem(previous.Id, previous with { RevokedAtUtc = fixture.Time.Now })
                } });
                break;
            case "old-retired":
                var rotations = accepted.OnboardingContracts!.VoterCredentialRotations!;
                VoterCredentialRotation rotation = rotations[artifact.IssueId];
                fixture.Subject.Publish(accepted with { OnboardingContracts = accepted.OnboardingContracts with
                {
                    VoterCredentialRotations = rotations.SetItem(artifact.IssueId, rotation with
                    {
                        CredentialRevision = accepted.Index, OverlapUntilUtc = fixture.Time.Now, RetiredIndex = accepted.Index
                    })
                } });
                break;
            case "recovery":
                fixture.SubjectBarrier.After = _ => fixture.Subject.BeginRecovery();
                break;
            case "proxy":
                fixture.SubjectOptions.TlsBoundary = "NginxIsolatedHttp";
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mutation));
        }

        UnauthorizedAccessException failure = await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            fixture.SubjectAuthority.ConfirmBindingAsync(command, signing, artifact, token).AsTask());

        Assert.Equal(reason, failure.Message);
        Assert.False(fixture.Subject.Current!.Results.ContainsKey(command.OperationId));
    }
}
