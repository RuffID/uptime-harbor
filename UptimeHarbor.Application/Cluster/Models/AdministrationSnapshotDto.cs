namespace UptimeHarbor.Application.Cluster.Models;

public record AdministrationSnapshotDto(Guid NodeId, Guid CurrentUserId, long Revision, DateTimeOffset AtUtc,
    IReadOnlyList<AdministrationEntityDto> Entities, IReadOnlyList<Guid> Nodes,
    IReadOnlyList<AdministrationConflictDto> Conflicts, IReadOnlyList<AdministrationTargetDto> Targets,
    IReadOnlyList<AdministrationDeliveryDto> Deliveries);
