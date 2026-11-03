using System.Collections.Immutable;

namespace UptimeHarbor.Application.Cluster.Models;

/// <summary>Локальный durable intent до внешнего эффекта; ключи представлены только локальной ссылкой.</summary>
public record LocalOnboardingAttempt(Guid AttemptId, EnrollmentRequest Request, string RequestDigest,
    string PrivateKeyReference, long Revision, ImmutableDictionary<string, Guid> OperationIds,
    string? ProtectedInvitation, long? ReservationIndex)
{
    public LocalOnboardingAttempt Reserved(long index)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(index);
        return this with { ProtectedInvitation = null, ReservationIndex = index, Revision = Revision + 1 };
    }

    public override string ToString()
    {
        return $"LocalOnboardingAttempt {AttemptId}";
    }
}
