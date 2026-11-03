namespace UptimeHarbor.Application.Installation;

/// <summary>Локальная подготовка отдельного общего аккаунта до issuer/genesis этапа 04.</summary>
public record BootstrapPreparation(Guid InstallIdentity, Guid AttemptId, Guid ClusterId,
    Guid NodeId, Guid StorageGeneration, string PeerAddress, string AccountDigest,
    string AccountReference, long BotId, long ProfileRevision, long RuntimeGeneration);
