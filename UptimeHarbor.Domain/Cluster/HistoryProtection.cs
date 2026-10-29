namespace UptimeHarbor.Domain.Cluster;

public enum HistoryProtection
{
    None = 0,
    OpenIncident = 1,
    OpenConflict = 2,
    Pending = 4,
    UnfinishedDelivery = 8,
    UnknownAttempt = 16,
    SummaryReference = 32,
    RecoveryEntitlement = 64,
    UnfinishedControl = 128,
    ObservationAssembly = 256,
    GenerationSafetyMarker = 512
}

