using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Cluster.Onboarding;
using UptimeHarbor.Application.Cluster.Onboarding.Renewal;
using UptimeHarbor.Domain.Cluster;
using UptimeHarbor.Infrastructure.Cluster.Onboarding;
using UptimeHarbor.Infrastructure.Cluster.Onboarding.Renewal;
using UptimeHarbor.Infrastructure.Cluster.Onboarding.Routing;
using UptimeHarbor.Infrastructure.Installation;
using UptimeHarbor.Infrastructure.Installation.Renewal;
using Xunit;

namespace UptimeHarbor.Tests.Web.Cluster.EnrollmentRouting;

public class VoterCredentialHandoffTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ScheduledPassUsesThePublishedLeafForPeerHandoffBeforeRetirement(bool typedRetirement)
    {
        // Arrange
        CancellationToken token = TestContext.Current.CancellationToken;
        await using EnrollmentRoutingFixture cluster = new() { UseManagedPools = true, RenewalEnabled = true };
        await cluster.InitializeAsync(token);
        EnrollmentRoutingNode source = cluster.Issuer;
        if (typedRetirement)
        {
            cluster.SetLeader(source);
        }

        var time = cluster.First.Time;
        RevisionImage image = cluster.State.Current!;
        var profile = source.Installation.Current!;
        Guid previous = profile.CredentialId!.Value;
        time.Now = image.Operations.Credentials[previous].ExpiresAtUtc.AddDays(-7);
        var contracts = image.OnboardingContracts!;
        var process = contracts.OnlineProcesses![source.Options.NodeId] with { LeaseUntilUtc = time.Now.AddMinutes(10) };
        cluster.State.Publish(image with { OnboardingContracts = contracts with
        {
            OnlineProcesses = contracts.OnlineProcesses.SetItem(process.NodeId, process)
        } });
        cluster.Refresh();

        var options = Options.Create(source.Options);
        VoterCredentialRenewalAuthority authority = new(cluster.State, source.Barrier, source.Nonces, options, time);
        LocalVoterCredentialFiles files = new(Options.Create(source.Local), source.Secrets);
        LocalVoterCredentialPreparation preparation = new(files, source.Secrets, source.Lease, source.Installation,
            authority, time, NullLogger<LocalVoterCredentialPreparation>.Instance);
        HandoffTransport transport = new(cluster, source);
        LocalVoterCredentialBindingCoordinator binding = new(preparation, files, source.Installation, authority,
            transport, time, NullLogger<LocalVoterCredentialBindingCoordinator>.Instance);
        LocalVoterCredentialInstallationCoordinator installed = new(binding, files, source.Installation,
            NullLogger<LocalVoterCredentialInstallationCoordinator>.Instance);
        LocalVoterCredentialPublication publication = new(installed, binding, files, source.Lease, source.Installation, cluster.State, source.Tls);
        LocalVoterCredentialRetirement retirement = new(files, installed, source.Tls, authority, transport, time, source.Client,
            typedRetirement ? source.RotationClient : null);
        LocalVoterRenewal renewal = new(source.Lease, source.Installation, source.Tls, preparation, authority, transport,
            source.VoterRouter, binding, installed, publication, options, time, retirement);

        var published = Assert.IsType<DynamicPeerTls.Snapshot>(await renewal.RunScheduledAsync(token));
        var rotation = VoterCredentialPolicy.Latest(cluster.State.Current!, source.Options.NodeId)!;
        Assert.Equal(published.CredentialRevision, rotation.CredentialRevision);
        Assert.Empty(cluster.Sent);
        Assert.Null(rotation.RetireOperationId);

        // Act
        time.Now = rotation.OverlapUntilUtc!.Value;
        cluster.RenewalClientLeaf = published.Certificate;
        cluster.RenewalIngressLeaf = published.Certificate;
        await renewal.RunScheduledAsync(token);
        await renewal.RunScheduledAsync(token);

        // Assert
        var exchange = Assert.Single(cluster.Sent);
        Assert.Equal(typedRetirement ? EnrollmentPeerProtocol.RETIREMENT_HANDOFF : EnrollmentPeerProtocol.HANDOFF, exchange.Path);
        Assert.NotEqual(exchange.Source, exchange.Target);
        var retired = VoterCredentialPolicy.Latest(cluster.State.Current!, source.Options.NodeId)!;
        Assert.NotNull(retired.RetireOperationId);
        if (typedRetirement)
        {
            Assert.Equal(["AuthorizeVoterCredential", "BindVoterCredential"], transport.Sent.Select(command => command.Action));
            var intent = StrictOnboardingJson.Decode<VoterCredentialRetirementIntent>(exchange.Body);
            Assert.Equal(intent.Command.OperationId, retired.RetireOperationId);
        }
        else
        {
            Assert.Equal(["AuthorizeVoterCredential", "BindVoterCredential", "RetireOldCredential"],
                transport.Sent.Select(command => command.Action));
            Assert.Equal(transport.Sent[^1].OperationId, retired.RetireOperationId);
        }

        Assert.Equal(time.Now, retired.RetiredAtUtc);
        Assert.Same(published, source.Tls.Capture());
    }

    [Theory]
    [InlineData("valid")]
    [InlineData("old-leaf")]
    [InlineData("wrong-revision")]
    [InlineData("changed-evidence")]
    [InlineData("leader-change")]
    public async Task HandoffRequiresBothPresentedLeavesAndCurrentBind(string condition)
    {
        // Arrange
        CancellationToken token = TestContext.Current.CancellationToken;
        await using VoterCredentialPeerFixture fixture = new();
        fixture.Cluster.UseManagedPools = true;
        await fixture.InitializeAsync(token);
        X509Certificate2 replacement = fixture.BindIssuerReplacement();
        DynamicPeerTls.Snapshot snapshot = fixture.PublishIssuerReplacement(replacement);
        RevisionImage image = fixture.Cluster.State.Current!;
        var rotation = VoterCredentialPolicy.Latest(image, fixture.Issuer.Options.NodeId)!;
        var witness = image.Operations.Nodes[fixture.Subject.Options.NodeId];
        Assert.NotEqual(fixture.Cluster.Leader.Options.NodeId, witness.NodeId);
        var handoff = new VoterCredentialHandoff(rotation.Renewal.Issuance.IssueId, rotation.CredentialRevision,
            rotation.BindDigest!, snapshot.Fingerprint);
        fixture.Cluster.RenewalClientLeaf = replacement;
        fixture.Cluster.RenewalIngressLeaf = replacement;
        fixture.Cluster.First.Time.Now = rotation.OverlapUntilUtc!.Value;
        Assert.False(EnrollmentLiveTrust.Validate(image, fixture.Issuer.Certificate, fixture.Cluster.First.Time.Now));
        Assert.True(EnrollmentLiveTrust.Validate(image, replacement, fixture.Cluster.First.Time.Now));

        if (condition == "old-leaf")
        {
            fixture.Cluster.RenewalIngressLeaf = fixture.Issuer.Certificate;
        }
        else if (condition == "wrong-revision")
        {
            handoff = handoff with { CredentialRevision = handoff.CredentialRevision + 1 };
        }
        else if (condition == "changed-evidence")
        {
            CommandResult bind = image.Results[rotation.BindOperationId!.Value];
            fixture.Cluster.AfterSend = (_, _) => fixture.Publish(image with
            {
                Results = image.Results.SetItem(bind.OperationId, bind with { AppliedRevision = bind.Index - 1 })
            });
        }
        else if (condition == "leader-change")
        {
            fixture.Cluster.AfterSend = (_, _) =>
            {
                fixture.Cluster.Term++;
                fixture.Cluster.SetLeader(fixture.Subject);
            };
        }

        // Act
        IVoterCredentialHandoffPeer peer = fixture.Issuer.Client;
        Exception? failure = await Record.ExceptionAsync(() => peer.ConfirmAsync(witness, handoff, snapshot, image, token));

        // Assert
        switch (condition)
        {
            case "valid":
                Assert.Null(failure);
                Assert.Equal(1, fixture.Cluster.ManagedHandshakes);
                Assert.Equal(EnrollmentPeerProtocol.HANDOFF, Assert.Single(fixture.Cluster.Sent).Path);
                break;
            case "old-leaf":
                Assert.IsType<HttpRequestException>(failure);
                break;
            case "leader-change":
                Assert.IsType<InvalidOperationException>(failure);
                break;
            default:
                Assert.IsType<UnauthorizedAccessException>(failure);
                break;
        }
    }

    private sealed class HandoffTransport(EnrollmentRoutingFixture cluster, EnrollmentRoutingNode source) : IRevisionTransport
    {
        internal List<RevisionCommand> Sent { get; } = [];

        public async Task<IReadOnlyList<CommandResult>> SendAsync(IReadOnlyList<RevisionCommand> commands, CancellationToken token)
        {
            RevisionCommand command = Assert.Single(commands);
            Sent.Add(command);
            if (command.Action == "RetireOldCredential")
            {
                Assert.Equal(EnrollmentPeerProtocol.HANDOFF, Assert.Single(cluster.Sent).Path);
            }

            RevisionApplyEngine apply = new(cluster.State, new(), cluster.Projection);
            RevisionCommand stamped = command with { EnrollmentTrust = command.EnrollmentTrust! with
            {
                CoordinatorAtUtc = cluster.First.Time.Now, CoordinatorTerm = cluster.Term
            } };
            await apply.ApplyAsync(stamped, cluster.State.Current!.Index + 1, cluster.Term, source.Options.NodeId, token);
            cluster.Refresh();
            CommandResult result = cluster.State.Current!.Results[command.OperationId];
            Assert.Equal(OperationOutcome.Committed, result.Outcome);
            return [result];
        }
    }
}
