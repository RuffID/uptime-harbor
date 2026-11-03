using System.Text.Json.Serialization;

namespace UptimeHarbor.Application.Cluster.Models;

/// <summary>Основания карантинного восстановления разделены; ни одно online основание не удостоверяет fencing.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(FreshBootstrapProof), "FreshBootstrap")]
[JsonDerivedType(typeof(FreshJoinProof), "FreshJoin")]
[JsonDerivedType(typeof(OnboardingResumeProof), "OnboardingResume")]
[JsonDerivedType(typeof(ManagedRestartProof), "ManagedRestart")]
[JsonDerivedType(typeof(SignedOfflineRecoveryProof), "SignedOfflineRecovery")]
public abstract record RecoveryProof;
