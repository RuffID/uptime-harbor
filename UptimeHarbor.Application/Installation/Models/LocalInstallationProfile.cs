using System.Collections.Immutable;

namespace UptimeHarbor.Application.Installation;

/// <summary>Неизменяемый локальный профиль; публикация разрешена только после commit.</summary>
public record LocalInstallationProfile(Guid InstallIdentity, string Mode, InstallationPhase Phase,
    long ProfileRevision, long RuntimeGeneration, long SetupEpoch, Guid BootId,
    DateTimeOffset LeaseUntilUtc, Guid? AttemptId = null, Guid? NodeId = null,
    Guid? StorageGeneration = null, string? PeerAddress = null, string? KeyReference = null,
    string? RequestDigest = null, string? ErrorCode = null,
    ImmutableDictionary<string, Guid>? OperationIds = null, string? Evidence = null,
    Guid? ClusterId = null, string? BootstrapAccountReference = null, long BotId = 0,
    string? BootstrapAccountDigest = null, long? ClusterGeneration = null,
    Guid? CredentialId = null, string? TrustAnchorFingerprint = null);
