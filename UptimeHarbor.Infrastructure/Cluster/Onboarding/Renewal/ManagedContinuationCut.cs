using static UptimeHarbor.Infrastructure.Cluster.Onboarding.Renewal.VoterContinuationWire;

namespace UptimeHarbor.Infrastructure.Cluster.Onboarding.Renewal;

/// <summary>Последний проверенный HTTPS срез в пределах исходного startup replay.</summary>
internal sealed record ManagedContinuationCut(ManagedContinuationReplay Replay, AcceptedSlice Slice, TimeProvider Time,
    long Started)
{
    internal bool IsCurrent()
    {
        DateTimeOffset now = Time.GetUtcNow();
        return Time.GetElapsedTime(Started) < TimeSpan.FromSeconds(10) && Replay.IsCurrentSlice(Slice)
            && now >= Slice.Issuer.NotBeforeUtc && now < Slice.Issuer.NotAfterUtc
            && (Slice.Registered is null || now < Slice.Process!.LeaseUntilUtc)
            && (Slice.Authorized is null || now >= Slice.Authorized.Command.CreatedAtUtc
                && now - Slice.Authorized.Command.CreatedAtUtc < TimeSpan.FromMinutes(5));
    }

    internal void RequireCurrent(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!IsCurrent())
        {
            throw new InvalidDataException("VoterContinuationStartupCutChanged");
        }
    }
}
