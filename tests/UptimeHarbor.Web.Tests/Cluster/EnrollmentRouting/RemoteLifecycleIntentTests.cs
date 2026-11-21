using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Cluster.Onboarding;
using UptimeHarbor.Web.Cluster;
using Xunit;

namespace UptimeHarbor.Tests.Web.Cluster.EnrollmentRouting;

public class RemoteLifecycleIntentTests
{
    [Fact]
    public async Task LifecycleRestoresPreRpcIntentWhenUnrelatedAcceptedPrefixHasAdvanced()
    {
        // Arrange
        ReplaySigner peer = new();
        await using ManagedBootstrapFixture fixture = new(signingPeers: peer);
        CancellationToken token = TestContext.Current.CancellationToken;
        await EnrollmentGatewayTests.ReadyAsync(fixture, token);
        RevisionImage image = fixture.Accepted.Current!;
        OnlineGrant old = image.OnboardingContracts!.OnlineGrants!.Values
            .Select(item => item.Grant).Single(item => item.Purpose == OnlineGrantPurpose.Process);
        fixture.Readiness.InvalidateRecovery();
        string identity = $"UptimeHarbor.ManagedLifecycle.v1/{old.AttemptId:D}/{fixture.Lease.BootId:D}/{fixture.Lease.BootChallenge}/RegisterOnlineProcess/{fixture.Readiness.RecoveryVersion}/{image.Operations.Revision}/{image.Operations.MembershipVersion}/{old.GrantId:D}";
        Guid operation = new(SHA256.HashData(Encoding.UTF8.GetBytes(identity)).AsSpan(0, 16));
        OnlineGrant original = old with
        {
            GrantId = operation, RecoveryVersion = fixture.Readiness.RecoveryVersion, MinimumIndex = image.Index,
            OperationsRevision = image.Operations.Revision, MembershipVersion = image.Operations.MembershipVersion,
            IssuedAtUtc = fixture.Time.Now, ExpiresAtUtc = fixture.Time.Now.AddMinutes(2), Signature = string.Empty
        };
        peer.Response = await fixture.Grants.SignAsync(original, token);
        await fixture.GrantRequests.PrepareAsync(original, token);

        // Только caller replay: synthetic remote owner и новый cut, без replacement/provisioning/сетевого signer.
        AcceptedEnrollmentIssuer issuer = image.OnboardingContracts.Issuers![original.IssuerEpoch];
        OperationalNode owner = image.Operations.Nodes[fixture.Options.NodeId] with { NodeId = Guid.NewGuid() };
        fixture.Accepted.Publish(image with
        {
            Index = image.Index + 1,
            Operations = image.Operations with { Nodes = image.Operations.Nodes.Add(owner.NodeId, owner) },
            OnboardingContracts = image.OnboardingContracts with
            {
                Issuers = image.OnboardingContracts.Issuers.SetItem(issuer.Epoch, issuer with { NodeId = owner.NodeId })
            }
        });
        fixture.Time.Now = fixture.Time.Now.AddSeconds(1);
        MethodInfo prepare = typeof(ManagedBootstrapLifecycle).GetMethod("PrepareAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;

        // Act
        Task<RevisionCommand> pending = (Task<RevisionCommand>)prepare.Invoke(fixture.Driver,
            [OnlineGrantPurpose.Process, "RegisterOnlineProcess", token])!;
        RevisionCommand command = await pending;

        // Assert
        Assert.Equal(operation, command.OperationId);
        Assert.Equal(original, peer.Request!.Grant);
        Assert.Equal(peer.Response, command.OnlineLifecycle!.Grant);
        Assert.Equal(original.IssuedAtUtc, command.CreatedAtUtc);
        Assert.False(fixture.Accepted.Current!.Results.ContainsKey(operation));
        Assert.Single(fixture.Store.Issues);
    }

    private class ReplaySigner : IEnrollmentPeerClient
    {
        public OnlineGrant? Response { get; set; }
        public OnlineGrantSigningRequest? Request { get; private set; }

        public Task<CommandResult> AcceptAsync(EnrollmentCoordinatedRequest request, CancellationToken token)
        {
            throw new InvalidOperationException("TestUnexpectedCoordinate");
        }

        public Task<EnrollmentIssuedArtifact> IssueAsync(Guid issuer, EnrollmentSigningRequest request, CancellationToken token)
        {
            throw new InvalidOperationException("TestUnexpectedCertificate");
        }

        public Task<OnlineGrant> SignGrantAsync(Guid issuer, OnlineGrantSigningRequest request, CancellationToken token)
        {
            Request = request;
            return Task.FromResult(Response ?? throw new InvalidOperationException("TestResponseMissing"));
        }
    }
}
