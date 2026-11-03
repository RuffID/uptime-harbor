using System.Collections.Immutable;

namespace UptimeHarbor.Application.Cluster.Models;

/// <summary>Часть прежнего принятого образа, без локальных секретов и полномочий runtime.</summary>
public record OnboardingImage
{
    public int Version { get; init; } = 1;
    public long Revision { get; init; }
    public ImmutableDictionary<Guid, OnboardingInvitation> Invitations { get; init; } = ImmutableDictionary<Guid, OnboardingInvitation>.Empty;
    public ImmutableDictionary<Guid, OnboardingRequestReceipt> Requests { get; init; } = ImmutableDictionary<Guid, OnboardingRequestReceipt>.Empty;
}
