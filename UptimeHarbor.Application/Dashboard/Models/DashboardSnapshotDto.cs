namespace UptimeHarbor.Application.Dashboard.Models;

public record DashboardSnapshotDto(
    DateTimeOffset GeneratedAtUtc,
    IReadOnlyList<DashboardServerDto> Servers);
