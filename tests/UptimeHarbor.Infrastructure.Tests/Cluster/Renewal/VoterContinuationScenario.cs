using System.Security.Cryptography;
using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Cluster.Onboarding;
using UptimeHarbor.Application.Cluster.Onboarding.Renewal;
using UptimeHarbor.Application.Installation;
using UptimeHarbor.Domain.Cluster;
using UptimeHarbor.Infrastructure.Cluster;
using UptimeHarbor.Infrastructure.Cluster.Onboarding;
using UptimeHarbor.Infrastructure.Cluster.Onboarding.Renewal;
using UptimeHarbor.Infrastructure.Identity;
using UptimeHarbor.Infrastructure.Installation;
using Xunit;

namespace UptimeHarbor.Tests.Infrastructure.Cluster.Renewal;

internal class VoterContinuationScenario : IDisposable, IClusterStateView, IConsensusWriter, ILocalAccountLogin, IVoterContinuationAdministrator
{
    internal VoterCredentialCryptoScenario Crypto { get; } = new();
    internal RevisionStateStore Store { get; } = new();
    internal InstallationState Installation { get; } = new();
    internal VoterCredentialAuthorityFixture.Barrier Barrier { get; } = new();
    internal VoterCredentialAuthorityFixture.Clock Time { get; }
    internal RevisionDispatcher Dispatcher { get; }
    internal VoterContinuationAuthority Authority { get; }
    internal VoterCredentialRequest Request { get; private set; }
    internal VoterCredentialProof Candidate { get; private set; }
    internal ClusterOptions Options { get; }
    internal RevisionImage State => Store.Current!;
    internal Guid Actor { get; } = Guid.NewGuid();
    internal bool AcceptPassword { get; set; } = true;
    internal Action? CompletePasswordCheck { get; set; }
    internal Action? BeforeApply { get; set; }
    internal bool LoseResponse { get; set; }
    internal bool NativeLeader { get; set; } = true;
    internal long Term { get; set; } = 1;
    internal List<byte[]> Wal { get; } = [];
    internal CancellationToken Token => TestContext.Current.CancellationToken;
    public Guid? CommonActorId { get; set; }

    public ConsensusStatus Consensus => new(State.ClusterId, State.Generation, Options.NodeId, Term,
        State.Operations.MembershipVersion, NativeLeader, State.Index, State.Operations.Nodes.Keys.ToHashSet(),
        CancellationToken.None, CancellationToken.None);

    public AcceptedClusterState Accepted => new(true, true, true, true, true, State.Index,
        State.Operations.MembershipVersion, Guid.Empty, false);

    internal VoterContinuationScenario(IOnboardingAudit? audit = null)
    {
        DateTimeOffset expiry = Crypto.Crypto.Template.NotAfterUtc;
        Time = new(expiry);
        OperationalNode audience = new(Guid.NewGuid(), Guid.NewGuid(), "https://audience.invalid/internal/raft",
            "audience", true, true, false, 1);
        RevisionImage image = Crypto.State;
        CommonActorId = Actor;
        AcceptedValue account = new(Actor, DeletedEntityKind.Account, 1, new()
        {
            RoleId = Guid.NewGuid(), Name = "admin", PasswordHash = "test-hash", SecurityStamp = "stamp", ConcurrencyStamp = "revision"
        });
        Store.Publish(image with
        {
            Operations = image.Operations with { Nodes = image.Operations.Nodes.Add(audience.NodeId, audience) },
            Values = image.Values.Add(Actor, account)
        });
        Options = new()
        {
            Mode = "Bootstrap", ClusterId = image.ClusterId, Generation = image.Generation, NodeId = audience.NodeId,
            StorageGeneration = audience.StorageGeneration, ProtocolMajor = 3, CommandSchema = 3, SnapshotFormat = 3, SqlSchema = 3,
            PublicUri = audience.Address
        };
        LocalInstallationProfile profile = new(Guid.NewGuid(), "Bootstrap", InstallationPhase.ClusterReady, 1, 1, 1,
            Guid.NewGuid(), expiry, NodeId: audience.NodeId, StorageGeneration: audience.StorageGeneration,
            PeerAddress: audience.Address, ClusterId: image.ClusterId, ClusterGeneration: image.Generation);
        Installation.Publish(profile);
        Installation.ConfirmCommonOpen(profile);
        EnrollmentIssuance template = Crypto.Intent.Issuance with { NotBeforeUtc = expiry.AddMinutes(-2), NotAfterUtc = expiry.AddDays(30) };
        Request = new(1, image.ClusterId, image.Generation, Guid.NewGuid(), Crypto.Intent with { Issuance = template });
        Candidate = new(1, VoterCredentialContinuationProofs.SCOPE, template.NodeId, template.StorageGeneration, Guid.NewGuid(),
            Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)), 1, 0, image.Operations.MembershipVersion, "", expiry, "");
        Dispatcher = new(this, Store, this, onboardingAudit: audit);
        Authority = new(Dispatcher, Store, Barrier, new(Time), this, Microsoft.Extensions.Options.Options.Create(Options),
            Installation, this, this, Time, new(Time));
    }

    internal async Task<VoterCredentialProofRequest> ProveAsync(string? authorization = null)
    {
        string nonce = await Authority.IssueNonceAsync(Request, Candidate, authorization, Token);
        VoterCredentialProof proof = VoterCredentialContinuationProofs.Sign(Request,
            Candidate with { IssuedAtUtc = Time.Now, Nonce = nonce }, Crypto.Crypto.PeerKey);
        return new(Request, proof, Convert.ToBase64String(Crypto.Csr));
    }

    internal async Task<CommandResult> ProposeAsync()
    {
        return await Authority.RequestAsync(await ProveAsync(), Token);
    }

    internal void UseProcess(InstallationLease lease)
    {
        Candidate = Candidate with { BootId = lease.BootId, BootChallenge = lease.BootChallenge };
    }

    internal Task<CommandResult> ApproveAsync(CommandResult proposed)
    {
        return Authority.AuthorizeAsync(Request.OperationId, proposed.Digest, "test-password", Token);
    }

    internal async Task<CommandResult> RegisterAsync(CommandResult authorized)
    {
        return await Authority.RegisterAsync(await ProveAsync(authorized.Digest), authorized.Digest, Token);
    }

    internal void NextProcess(VoterCredentialProof? historical = null)
    {
        Request = Request with
        {
            OperationId = Guid.NewGuid(),
            Renewal = Request.Renewal with { Issuance = Request.Renewal.Issuance with { IssueId = Guid.NewGuid() } }
        };
        Candidate = Candidate with
        {
            BootId = historical?.BootId ?? Guid.NewGuid(),
            BootChallenge = historical?.BootChallenge ?? Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
            ProcessEpoch = Candidate.ProcessEpoch + 1
        };
    }

    internal void ChangeActor(bool enabled)
    {
        AcceptedValue value = State.Values[Actor];
        Store.Publish(State with { Values = State.Values.SetItem(Actor, value with { Revision = value.Revision + 1, Value = value.Value with { Enabled = enabled } }) });
    }

    public Task<bool> CheckAsync(SharedAccount account, string password, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        CompletePasswordCheck?.Invoke();
        return Task.FromResult(AcceptPassword && password == "test-password");
    }

    public ValueTask<long> ReplicateAsync(ReadOnlyMemory<byte> payload, int commandId, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        Assert.Equal(RevisionCodec.COMMAND_ID, commandId);
        BeforeApply?.Invoke();
        RevisionCommand command = RevisionCodec.Decode(payload.Span);
        Wal.Add(payload.ToArray());
        RevisionImage next = new RevisionReducer().Apply(State, command, State.Index + 1, Term);
        Store.Publish(next);
        if (LoseResponse)
        {
            throw new IOException("Test response lost after apply");
        }

        return ValueTask.FromResult(next.Index);
    }

    public ValueTask<bool> ResignAsync(CancellationToken token)
    {
        throw new InvalidOperationException("Test transport must not resign");
    }

    public void Dispose()
    {
        Crypto.Dispose();
    }
}
