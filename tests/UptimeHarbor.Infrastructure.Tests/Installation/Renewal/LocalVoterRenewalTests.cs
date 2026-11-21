using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Cluster.Onboarding.Renewal;
using UptimeHarbor.Application.Installation;
using UptimeHarbor.Domain.Cluster;
using UptimeHarbor.Infrastructure.Cluster;
using UptimeHarbor.Infrastructure.Cluster.Onboarding;
using UptimeHarbor.Infrastructure.Installation;
using UptimeHarbor.Infrastructure.Installation.Renewal;
using UptimeHarbor.Tests.Infrastructure.Cluster.Renewal;
using Xunit;

namespace UptimeHarbor.Tests.Infrastructure.Installation.Renewal;

public class LocalVoterRenewalTests
{
    [Fact]
    public async Task ScheduledPassStartsAtSevenDaysAndDoesNotStartAgainDuringOverlap()
    {
        using Scenario scenario = new();
        VoterCredentialAuthorityFixture authority = scenario.Local.Authority;
        DateTimeOffset due = authority.Scenario.State.Operations.Credentials[scenario.Baseline].ExpiresAtUtc.AddDays(-7);
        authority.Time.Now = due.AddTicks(-1);

        Assert.Null(await scenario.RunScheduledAsync());
        Assert.Empty(scenario.Local.Files.Writes);
        Assert.Empty(scenario.Transport.Sent);

        authority.Time.Now = due;
        scenario.PublishProcess(epoch: 3);
        DynamicPeerTls.Snapshot? published = await scenario.RunScheduledAsync();

        Assert.NotNull(published);
        Assert.Same(published, scenario.Tls.Capture());
        Assert.Null(await scenario.RunScheduledAsync());
        Assert.Equal(["AuthorizeVoterCredential", "BindVoterCredential"], scenario.Transport.Sent.Select(item => item.Action));
        Assert.Equal(1, authority.Remote.Calls);
    }

    [Theory]
    [InlineData("common-closed")]
    [InlineData("proxy")]
    [InlineData("expired")]
    [InlineData("majority")]
    public async Task ScheduledPassHasNoEffectsWithoutCurrentAuthority(string failure)
    {
        using Scenario scenario = new();
        VoterCredentialAuthorityFixture authority = scenario.Local.Authority;
        if (failure == "expired")
        {
            authority.Time.Now = authority.Scenario.State.Operations.Credentials[scenario.Baseline].ExpiresAtUtc;
        }

        if (failure == "proxy")
        {
            authority.SubjectOptions.TlsBoundary = "NginxIsolatedHttp";
        }

        authority.SubjectBarrier.Available = failure != "majority";
        if (failure == "majority")
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => scenario.RunScheduledAsync());
        }
        else
        {
            Assert.Null(await scenario.RunScheduledAsync(openCommon: failure != "common-closed"));
        }

        Assert.Empty(scenario.Local.Files.Writes);
        Assert.Empty(scenario.Transport.Sent);
        Assert.Equal(0, authority.Remote.Calls);
    }

    [Fact]
    public async Task ScheduledPassResumesExactAuthorizationFollowingIssuerOutage()
    {
        using Scenario scenario = new();
        VoterCredentialAuthorityFixture authority = scenario.Local.Authority;
        authority.Remote.Available = false;

        await Assert.ThrowsAsync<IOException>(() => scenario.RunScheduledAsync());
        RevisionCommand authorize = Assert.Single(scenario.Transport.Sent);
        Assert.Empty(authority.Journal.Issued);

        authority.Time.Now = authority.Time.Now.AddMinutes(1);
        authority.Remote.Available = true;
        scenario.Reopen(restoreBaseline: true);
        DynamicPeerTls.Snapshot? published = await scenario.RunScheduledAsync();

        Assert.NotNull(published);
        Assert.Equal(authorize.EnrollmentTrust!.Rotation!.Renewal.Issuance.IssueId, published.CredentialId);
        Assert.Same(authorize, scenario.Transport.Sent[0]);
        Assert.Equal(2, scenario.Transport.Sent.Count);
        Assert.Equal(1, authority.Journal.Commits);
    }

    [Fact]
    public async Task ScheduledPassCompletesAcceptedBindAfterOldLeafHasExpired()
    {
        using Scenario scenario = new();
        VoterCredentialAuthorityFixture authority = scenario.Local.Authority;
        scenario.Transport.FailAfter = "bind-response";

        await Assert.ThrowsAsync<IOException>(() => scenario.RunScheduledAsync());
        VoterCredentialRenewal accepted = scenario.Transport.Sent[1].EnrollmentTrust!.Rotation!.Renewal;
        authority.Time.Now = authority.Scenario.State.Operations.Credentials[scenario.Baseline].ExpiresAtUtc;
        scenario.Reopen(restoreBaseline: false);
        DynamicPeerTls.Snapshot? published = await scenario.RunScheduledAsync();

        Assert.NotNull(published);
        Assert.Equal(accepted.Issuance.IssueId, published.CredentialId);
        Assert.Equal(2, scenario.Transport.Sent.Count);
        Assert.Equal(1, authority.Remote.Calls);
    }

    [Fact]
    public async Task SecondRotationUsesPublishedCredentialAndProtectedKey()
    {
        // Arrange
        using Scenario scenario = new();

        DynamicPeerTls.Snapshot first = await scenario.RenewAsync(scenario.Baseline);
        scenario.Retire();
        scenario.Local.Authority.Time.Now = new DateTimeOffset(first.Certificate.NotAfter.ToUniversalTime()).AddDays(-7);
        scenario.PublishProcess(epoch: 4);

        // Act
        DynamicPeerTls.Snapshot second = await scenario.RenewAsync(first.CredentialId!.Value);

        // Assert
        VoterCredentialRenewal secondAuthorization = scenario.Transport.Sent[2].EnrollmentTrust!.Rotation!.Renewal;
        Assert.Equal(first.CredentialId, secondAuthorization.PreviousCredentialId);
        Assert.Equal(first.CredentialRevision, secondAuthorization.PreviousCredentialRevision);
        Assert.Equal(first.Fingerprint, secondAuthorization.PreviousFingerprint);
        Assert.NotEqual(first.CredentialId, second.CredentialId);
        Assert.True(second.CredentialRevision > first.CredentialRevision);
        Assert.Same(second, scenario.Tls.Capture());
        Assert.Equal(2, scenario.Local.Authority.Journal.Commits);
        Assert.Equal(4, scenario.Transport.Sent.Count);
    }

    [Theory]
    [InlineData("authorize-response")]
    [InlineData("authorization")]
    [InlineData("issuer-response")]
    [InlineData("artifact")]
    [InlineData("bind-response")]
    [InlineData("bind-result")]
    [InlineData("install-confirmation")]
    [InlineData("publication")]
    public async Task ResumeKeepsOriginalAcceptedOperationsAndDer(string boundary)
    {
        // Arrange
        using Scenario scenario = new();

        scenario.Transport.FailAfter = boundary;
        scenario.Local.Authority.Remote.LoseResponse = boundary == "issuer-response";
        scenario.Local.Files.FailStage = boundary;
        scenario.Local.Files.AfterWrite = true;
        Action interruptPublish = () =>
        {
            if (boundary == "publication" && scenario.Local.Files.Writes.Contains("install-confirmation"))
            {
                throw new IOException("InterruptedPublication");
            }
        };
        // Act
        Exception? interruption;
        using (new RuntimeGenerationContext(interruptPublish))
        {
            interruption = await Record.ExceptionAsync(() => scenario.RenewAsync(scenario.Baseline));
        }

        // Assert
        Assert.IsType<IOException>(interruption);

        // Arrange
        RevisionCommand authorize = scenario.Transport.Sent[0];
        Guid issue = authorize.EnrollmentTrust!.Rotation!.Renewal.Issuance.IssueId;
        string? der = scenario.Local.Authority.Journal.Issued.GetValueOrDefault(issue)?.CertificateDigest;
        scenario.Transport.FailAfter = null;
        scenario.Local.Authority.Remote.LoseResponse = false;
        scenario.Local.Files.FailStage = null;
        scenario.Reopen(restoreBaseline: boundary is "authorize-response" or "authorization" or "issuer-response" or "artifact");

        // Act
        DynamicPeerTls.Snapshot published = await scenario.RenewAsync(scenario.Baseline);
        DynamicPeerTls.Snapshot replay = await scenario.RenewAsync(scenario.Baseline);

        // Assert
        Assert.Equal(issue, published.CredentialId);
        Assert.Equal(der ?? scenario.Local.Authority.Journal.Issued[issue].CertificateDigest, published.Fingerprint);
        Assert.Same(published, replay);
        Assert.Equal(["AuthorizeVoterCredential", "BindVoterCredential"], scenario.Transport.Sent.Select(item => item.Action));
        Assert.Equal(1, scenario.Local.Authority.Journal.Commits);
        int expectedSignerCalls = 1;
        if (boundary == "issuer-response")
        {
            expectedSignerCalls = 2;
        }

        Assert.Equal(expectedSignerCalls, scenario.Local.Authority.Remote.Calls);
    }

    [Fact]
    public async Task PublishedInMemoryKeyDoesNotReplaceMissingProtectedSlot()
    {
        // Arrange
        using Scenario scenario = new();

        DynamicPeerTls.Snapshot first = await scenario.RenewAsync(scenario.Baseline);
        scenario.Retire();
        scenario.Local.Authority.Time.Now = new DateTimeOffset(first.Certificate.NotAfter.ToUniversalTime()).AddDays(-7);
        scenario.PublishProcess(epoch: 4);
        File.Delete(scenario.Local.PathFor("intent"));

        // Act
        Exception? error = await Record.ExceptionAsync(() => scenario.RenewAsync(first.CredentialId!.Value));

        // Assert
        Assert.IsType<InvalidDataException>(error);
        Assert.Same(first, scenario.Tls.Capture());
        Assert.Equal(2, scenario.Transport.Sent.Count);
        Assert.Single(scenario.Local.Authority.Journal.Issued);
    }

    [Fact]
    public async Task LostPublishResultReopensConfirmedSlotWithoutAnotherTransition()
    {
        // Arrange
        using Scenario scenario = new();

        DynamicPeerTls.Snapshot first = await scenario.RenewAsync(scenario.Baseline);
        scenario.Reopen(restoreBaseline: false);

        // Act
        DynamicPeerTls.Snapshot restored = await scenario.RenewAsync(scenario.Baseline);

        // Assert
        Assert.NotSame(first, restored);
        Assert.Equal(first.CredentialId, restored.CredentialId);
        Assert.Equal(first.CredentialRevision, restored.CredentialRevision);
        Assert.Equal(first.Fingerprint, restored.Fingerprint);
        Assert.Equal(2, scenario.Transport.Sent.Count);
        Assert.Equal(1, scenario.Local.Authority.Remote.Calls);
    }

    [Theory]
    [InlineData("proxy")]
    [InlineData("majority")]
    [InlineData("issuer")]
    [InlineData("process")]
    [InlineData("expired")]
    [InlineData("revoked")]
    public async Task RefusalLeavesProtectedStoreAndTransportUntouched(string failure)
    {
        // Arrange
        using Scenario scenario = new();

        VoterCredentialAuthorityFixture authority = scenario.Local.Authority;
        switch (failure)
        {
            case "proxy":
                authority.SubjectOptions.TlsBoundary = "NginxIsolatedHttp";
                break;
            case "majority":
                authority.SubjectBarrier.Available = false;
                break;
            case "issuer":
                RevisionImage current = authority.Scenario.State;
                OnboardingContractImage contracts = current.OnboardingContracts!;
                Guid epoch = authority.Scenario.Crypto.Epoch;
                var issuers = contracts.Issuers!;
                authority.Scenario.State = current with
                {
                    OnboardingContracts = contracts with
                    {
                        Issuers = issuers.SetItem(epoch, issuers[epoch] with { RevokedAtUtc = authority.Time.Now })
                    }
                };
                authority.Publish();
                break;
            case "process":
                authority.Scenario.State = authority.Scenario.State with { OnboardingContracts =
                    authority.Scenario.State.OnboardingContracts! with { OnlineProcesses = null } };
                authority.Publish();
                break;
            case "expired":
                authority.Time.Now = authority.Scenario.State.Operations.Credentials[scenario.Baseline].ExpiresAtUtc;
                break;
            case "revoked":
                RevisionImage image = authority.Scenario.State;
                OperationalCredential leaf = image.Operations.Credentials[scenario.Baseline];
                authority.Scenario.State = image with { Operations = image.Operations with
                    { Credentials = image.Operations.Credentials.SetItem(leaf.Id, leaf with { RevokedAtUtc = authority.Time.Now }) } };
                authority.Publish();
                break;
        }

        // Act
        Exception? error = await Record.ExceptionAsync(() => scenario.RenewAsync(scenario.Baseline));

        // Assert
        Assert.NotNull(error);
        Assert.Empty(scenario.Local.Files.Writes);
        Assert.Empty(scenario.Transport.Sent);
        Assert.Equal(0, authority.Remote.Calls);
    }

    internal sealed class Scenario : IDisposable
    {
        private LocalVoterRenewal renewal;
        internal LocalVoterRenewal Renewal => renewal;
        public LocalVoterCredentialFixture Local { get; }
        public RotationTransport Transport { get; }
        public DynamicPeerTls Tls { get; private set; }
        public Guid Baseline => Local.Authority.Scenario.Intent.PreviousCredentialId;

        public Scenario(IVoterCredentialHandoffPeer? peer = null, bool remoteIssuer = true)
        {
            Local = new(remoteIssuer: remoteIssuer);
            Transport = new(this);
            this.peer = peer;
            (renewal, Tls) = Compose(restoreBaseline: true, epoch: 3);
        }

        private readonly IVoterCredentialHandoffPeer? peer;

        public void Reopen(bool restoreBaseline)
        {
            Local.Reopen();
            (renewal, Tls) = Compose(restoreBaseline, epoch: 4);
        }

        private (LocalVoterRenewal Renewal, DynamicPeerTls Tls) Compose(bool restoreBaseline, long epoch)
        {
            VoterCredentialAuthorityFixture authority = Local.Authority;
            IOptions<ClusterOptions> options = Options.Create(authority.SubjectOptions);
            OperationalNode node = authority.Scenario.State.Operations.Nodes[authority.SubjectId];
            LocalInstallationProfile profile = new(Local.Lease.InstallIdentity, "Bootstrap", InstallationPhase.ClusterReady,
                4, 3, 1, Local.Lease.BootId, authority.Time.Now.AddHours(1),
                AttemptId: Local.PreviousKeyOwner, NodeId: node.NodeId, StorageGeneration: node.StorageGeneration,
                PeerAddress: node.Address, KeyReference: Local.PreviousKeyReference, ClusterId: authority.Request.ClusterId,
                ClusterGeneration: authority.Request.Generation, CredentialId: Baseline,
                TrustAnchorFingerprint: EnrollmentCertificates.Hash(authority.Scenario.Crypto.Issuer.RawData));
            Local.State.Publish(profile);
            DynamicPeerTls tls = new(Local.State, authority.Subject, authority.Time);
            if (restoreBaseline)
            {
                tls.Install(profile.RuntimeGeneration, authority.Scenario.Previous.CopyWithPrivateKey(authority.Scenario.Crypto.PeerKey),
                    authority.Scenario.Crypto.Issuer, [authority.Scenario.Intent.PreviousFingerprint], _ => false, authority.Scenario.Crypto.Epoch);
            }

            PublishProcess(epoch);
            LocalVoterCredentialBindingCoordinator binding = new(Local.Store, Local.Files, Local.State, authority.SubjectAuthority,
                Transport, authority.Time, NullLogger<LocalVoterCredentialBindingCoordinator>.Instance);
            LocalVoterCredentialInstallationCoordinator installed = new(binding, Local.Files, Local.State,
                NullLogger<LocalVoterCredentialInstallationCoordinator>.Instance);
            LocalVoterCredentialPublication publication = new(installed, binding, Local.Files, Local.Lease, Local.State, authority.Subject, tls);
            LocalVoterCredentialRetirement retirement = new(Local.Files, installed, tls, authority.SubjectAuthority, Transport, authority.Time, peer);

            return (new(Local.Lease, Local.State, tls, Local.Store, authority.SubjectAuthority, Transport,
                authority.Router(), binding, installed, publication, options, authority.Time, retirement), tls);
        }

        public Task<DynamicPeerTls.Snapshot> RenewAsync(Guid previous)
        {
            return renewal.RenewAsync(previous, Local.Authority.Scenario.Crypto.Epoch, Local.Token);
        }

        public Task<DynamicPeerTls.Snapshot?> RunScheduledAsync(bool openCommon = true)
        {
            if (openCommon)
            {
                Local.State.ConfirmCommonOpen(Local.State.Current!);
            }

            return renewal.RunScheduledAsync(Local.Token);
        }

        public void Retire()
        {
            RevisionCommand bind = Transport.Sent.Last();
            RevisionCommand retire = bind with
            {
                Action = "RetireOldCredential", OperationId = Guid.NewGuid(),
                EnrollmentTrust = bind.EnrollmentTrust! with
                {
                    Rotation = new(bind.EnrollmentTrust.Rotation!.Renewal, null, new string('A', 64))
                }
            };
            Assert.Equal(OperationOutcome.Committed, Transport.Commit(retire).Outcome);
        }

        public void PublishProcess(long epoch)
        {
            VoterCredentialAuthorityFixture authority = Local.Authority;
            RevisionImage image = authority.Scenario.State;
            OnboardingContractImage contracts = image.OnboardingContracts!;
            OnlineProcessLease process = contracts.OnlineProcesses![authority.SubjectId] with
            {
                BootId = Local.Lease.BootId, BootChallenge = Local.Lease.BootChallenge, ProcessEpoch = epoch,
                LeaseUntilUtc = authority.Time.Now.AddMinutes(10)
            };
            authority.Scenario.State = image with
            {
                OnboardingContracts = contracts with { OnlineProcesses = contracts.OnlineProcesses.SetItem(process.NodeId, process) }
            };
            authority.Publish();
        }

        public void Dispose()
        {
            Local.Dispose();
        }
    }

    internal sealed class RotationTransport(Scenario scenario) : IRevisionTransport
    {
        public List<RevisionCommand> Sent { get; } = [];
        public string? FailAfter { get; set; }
        public OnboardingAudit? Audit { get; set; }
        public Action<RevisionCommand>? BeforeCommit { get; set; }
        public Action<RevisionCommand>? AfterCommit { get; set; }

        public Task<IReadOnlyList<CommandResult>> SendAsync(IReadOnlyList<RevisionCommand> commands, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            RevisionCommand command = Assert.Single(commands);
            Assert.Contains(command.Action, new[] { "AuthorizeVoterCredential", "BindVoterCredential", "RetireOldCredential" });
            Sent.Add(command);
            BeforeCommit?.Invoke(command);
            CommandResult result = Commit(command);
            AfterCommit?.Invoke(command);
            if ((command.Action == "AuthorizeVoterCredential" && FailAfter == "authorize-response")
                || (command.Action == "BindVoterCredential" && FailAfter == "bind-response")
                || (command.Action == "RetireOldCredential" && FailAfter == "retire-response"))
            {
                throw new IOException("SimulatedLostRotationResponse");
            }

            return Task.FromResult<IReadOnlyList<CommandResult>>([result]);
        }

        public CommandResult Commit(RevisionCommand command, DateTimeOffset? at = null)
        {
            VoterCredentialAuthorityFixture authority = scenario.Local.Authority;
            RevisionImage before = authority.Scenario.State;
            RevisionCommand accepted = command with
            {
                EnrollmentTrust = command.EnrollmentTrust! with
                {
                    CoordinatorAtUtc = at ?? authority.Time.Now, CoordinatorTerm = authority.Scenario.State.Term
                }
            };
            CommandResult result = authority.Scenario.Apply(accepted);
            authority.Publish();
            Audit?.Accepted(before, authority.Scenario.State, accepted, result);
            return result;
        }
    }
}
