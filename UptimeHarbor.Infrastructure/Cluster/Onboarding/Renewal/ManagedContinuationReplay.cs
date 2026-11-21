using UptimeHarbor.Application.Cluster.Models;
using static UptimeHarbor.Infrastructure.Cluster.Onboarding.Renewal.VoterContinuationWire;

namespace UptimeHarbor.Infrastructure.Cluster.Onboarding.Renewal;

/// <summary>Local replay evidence for one start attempt, scoped to its protected profile and process.</summary>
public sealed class ManagedContinuationReplay
{
    private AcceptedSlice? last;
    private readonly object sync = new();

    internal ManagedContinuationReplay(RevisionImage image, long nativeTerm, Guid nodeId, ManagedRestartProof process,
        long recoveryVersion, Func<bool> isCurrent)
    {
        Image = image;
        NativeTerm = nativeTerm;
        NodeId = nodeId;
        Process = process;
        RecoveryVersion = recoveryVersion;
        IsCurrent = isCurrent;
    }

    internal RevisionImage Image { get; }
    internal long NativeTerm { get; }
    internal Guid NodeId { get; }
    internal ManagedRestartProof Process { get; }
    internal long RecoveryVersion { get; }
    private Func<bool> IsCurrent { get; }

    internal void RequireCurrent(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!IsCurrent())
        {
            throw new InvalidDataException("VoterContinuationLocalReplayChanged");
        }
    }

    internal bool IsCurrentSlice(AcceptedSlice slice)
    {
        lock (sync)
        {
            return ReferenceEquals(last, slice) && IsCurrent();
        }
    }

    internal void Verify(AcceptedSlice slice, SliceQuery query, string audience, DateTimeOffset now, CancellationToken token)
    {
        lock (sync)
        {
            RequireCurrent(token);
            VoterContinuationSlice.Verify(slice, query, Image, audience, last, now);
            RequireCurrent(token);
            last = slice;
        }
    }
}
