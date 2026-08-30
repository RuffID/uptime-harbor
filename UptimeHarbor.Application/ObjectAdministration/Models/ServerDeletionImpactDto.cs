namespace UptimeHarbor.Application.ObjectAdministration.Models;

public record ServerDeletionImpactDto(Guid ServerId, int ContainerCount, long Version);
