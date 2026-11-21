using System.Text.Json.Serialization;

namespace UptimeHarbor.Application.Cluster.Models;

/// <summary>Основание ручного восстановления проверяется до открытия хранилища.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(SignedOfflineRecoveryProof), "SignedOfflineRecovery")]
public abstract record RecoveryProof;
