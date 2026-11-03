using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Installation;
using UptimeHarbor.Domain.Cluster;
using UptimeHarbor.Infrastructure.Cluster;
using UptimeHarbor.Infrastructure.Cluster.Onboarding;
using UptimeHarbor.Infrastructure.Installation;
using Xunit;

namespace UptimeHarbor.Tests.Infrastructure.Installation;

internal sealed class GenesisFixture : IDisposable
{
    public InstallationFixture Local { get; } = new();
    public EnrollmentSigningJournalTests.MemoryWork Signing { get; } = new();
    public GenesisPreparation Preparation { get; }
    public DynamicPeerTls Tls { get; }
    public ManagedAdmissionState Admission { get; }
    public GenesisWriterPermit Permit { get; } = new();
    public RevisionStateStore Accepted { get; } = new();
    public RevisionCommand Account { get; }

    public GenesisFixture()
    {
        Guid node = Guid.NewGuid();
        Guid actor = Guid.NewGuid();
        Guid attempt = Guid.NewGuid();
        Account = new()
        {
            ClusterId = Guid.NewGuid(), ClusterGeneration = 1, OperationId = Guid.NewGuid(), OriginNodeId = node,
            ActorUserId = actor, ActorRevision = 1, EntityId = actor, EntityKind = DeletedEntityKind.Account,
            Action = "BootstrapAccount", BaseRevision = 0, CreatedAtUtc = Local.Time.Now,
            Value = new() { Name = "common", RoleId = Guid.NewGuid(), PasswordHash = "test-hash", SecurityStamp = "test-stamp", ConcurrencyStamp = "test-concurrency" }
        };
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        byte[] privatePem = Encoding.UTF8.GetBytes(key.ExportPkcs8PrivateKeyPem());
        string keyReference;
        try
        {
            keyReference = Local.Secrets.Write(attempt, "peer", privatePem);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(privatePem);
        }

        string accountReference = Local.Secrets.Write(attempt, "bootstrap", RevisionCodec.Encode(Account));
        LocalInstallationProfile profile = new(Local.Lease.InstallIdentity, "Bootstrap", InstallationPhase.LocalPrepared,
            1, 1, 1, Local.Lease.BootId, Local.Time.Now.AddMinutes(2))
        {
            ClusterId = Account.ClusterId, ClusterGeneration = 1, NodeId = node, AttemptId = attempt,
            StorageGeneration = Guid.NewGuid(), PeerAddress = Local.Options.PeerEndpoint, KeyReference = keyReference,
            BootstrapAccountReference = accountReference, BootstrapAccountDigest = RevisionCodec.Digest(Account), BotId = 123,
            OperationIds = new Dictionary<string, Guid> { ["bootstrap"] = Account.OperationId, ["admit"] = Guid.NewGuid() }.ToImmutableDictionary()
        };
        Local.Store.Profile = profile;
        Local.State.Publish(profile);
        Preparation = new(Local.Store, Signing, Local.Secrets, Local.Lease, Options.Create(Local.Options), Local.Time);
        Tls = new(Local.State);
        Admission = new(Local.State, Options.Create(Local.Options), Options.Create(new ClusterControlOptions()), Local.Lease);
    }

    public async Task<SetupAuthorityEvidence> PrepareAsync()
    {
        LocalInstallationProfile profile = Local.Store.Profile!;
        return await Preparation.PrepareAsync(new(profile.InstallIdentity, profile.AttemptId!.Value,
            profile.ClusterId!.Value, profile.NodeId!.Value, profile.StorageGeneration!.Value, profile.PeerAddress!,
            profile.BootstrapAccountDigest!, profile.BootstrapAccountReference!, profile.BotId,
            profile.ProfileRevision, profile.RuntimeGeneration), TestContext.Current.CancellationToken);
    }

    public async Task<LocalInstallationProfile> SaveAsync(SetupAuthorityEvidence evidence)
    {
        LocalInstallationProfile profile = Local.Store.Profile! with
        {
            Evidence = JsonSerializer.Serialize(evidence), ProfileRevision = Local.Store.Profile!.ProfileRevision + 1,
            CredentialId = evidence.Genesis!.Template.IssueId, TrustAnchorFingerprint = evidence.Genesis.Authorization.IssuerCertificateDigest
        };
        await Signing.SaveChangesAsync(TestContext.Current.CancellationToken);
        Local.Store.Profile = profile;
        Local.State.Publish(profile);
        return profile;
    }

    public GenesisRuntimePreparation Runtime()
    {
        LocalInstallationProfile profile = Local.Store.Profile! with
        {
            Phase = InstallationPhase.ClusterQuarantine, ProfileRevision = Local.Store.Profile!.ProfileRevision + 1
        };
        Local.Store.Profile = profile;
        Local.State.Publish(profile);
        return new(Local.Store, Signing, Local.Secrets, Local.State, Local.Lease, Tls, Admission,
            Permit, Accepted, Options.Create(Local.Cluster), Local.Time);
    }

    public void Dispose()
    {
        Tls.Revoke();
        Local.Dispose();
    }
}
