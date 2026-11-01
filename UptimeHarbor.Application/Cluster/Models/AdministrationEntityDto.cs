namespace UptimeHarbor.Application.Cluster.Models;

public record AdministrationEntityDto(Guid Id, string Kind, long Revision, bool Deleted,
    AdministrationSettingsDto Settings);
