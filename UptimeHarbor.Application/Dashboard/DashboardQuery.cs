using UptimeHarbor.Application.Common;
using UptimeHarbor.Application.Dashboard.Models;
using UptimeHarbor.Application.Security;

namespace UptimeHarbor.Application.Dashboard;

public class DashboardQuery(
    IUserAccessContext accessContext,
    IDashboardSnapshotReadQuery snapshotReadQuery,
    TimeProvider timeProvider,
    IAdministratorAccessPolicy administratorPolicy)
    : IDashboardQuery
{
    private const string MISSING_NAME = "Имя не получено";

    public async Task<ServiceResult<DashboardSnapshotDto>> GetSnapshotAsync(
        string? search,
        CancellationToken cancellationToken = default)
    {
        ServiceError? accessError = administratorPolicy.GetAccessError();
        if (accessError is not null)
        {
            return ServiceResult<DashboardSnapshotDto>.Fail(accessError);
        }

        DashboardReadSnapshot readSnapshot = await snapshotReadQuery.GetSnapshotAsync(
            accessContext.UserId,
            true,
            cancellationToken);
        IReadOnlyList<DashboardServerReadRecord> accessibleServers = readSnapshot.Servers;
        IReadOnlyList<DashboardContainerReadRecord> accessibleContainers = readSnapshot.Containers;

        string? normalizedSearch = null;
        if (!string.IsNullOrWhiteSpace(search))
        {
            normalizedSearch = search.Trim();
        }

        DateTimeOffset generatedAtUtc = timeProvider.GetUtcNow();
        EnsureUtc(generatedAtUtc, nameof(generatedAtUtc));

        List<DashboardServerDto> servers = [];
        foreach (DashboardServerReadRecord server in accessibleServers.OrderBy(server => server.Name))
        {
            bool serverMatches = Matches(server, normalizedSearch);
            IEnumerable<DashboardContainerReadRecord> serverContainers = accessibleContainers
                .Where(container => container.ServerId == server.Id);

            if (normalizedSearch is not null && !serverMatches)
            {
                serverContainers = serverContainers.Where(container => Matches(container, normalizedSearch));
            }

            List<DashboardContainerReadRecord> matchingContainers = serverContainers
                .OrderBy(GetDisplayName)
                .ToList();
            if (normalizedSearch is not null && !serverMatches && matchingContainers.Count == 0)
            {
                continue;
            }

            servers.Add(MapServer(server, matchingContainers, generatedAtUtc));
        }

        List<DashboardContainerDto> unassignedContainers = accessibleContainers
            .Where(container => container.ServerId is null && Matches(container, normalizedSearch))
            .OrderBy(GetDisplayName)
            .Select(container => MapContainer(container, generatedAtUtc))
            .ToList();

        return ServiceResult<DashboardSnapshotDto>.Ok(
            new DashboardSnapshotDto(generatedAtUtc, servers, unassignedContainers));
    }

    private static DashboardServerDto MapServer(
        DashboardServerReadRecord server,
        IReadOnlyList<DashboardContainerReadRecord> containers,
        DateTimeOffset generatedAtUtc)
    {
        EnsureUtc(server.LastCheckedAtUtc, nameof(server.LastCheckedAtUtc));
        EnsureUtc(server.StartedAtUtc, nameof(server.StartedAtUtc));

        long? uptimeSeconds = null;
        if (server.StartedAtUtc is DateTimeOffset startedAtUtc)
        {
            TimeSpan uptime = generatedAtUtc - startedAtUtc;
            if (uptime < TimeSpan.Zero)
            {
                throw new InvalidOperationException(
                    "Время запуска сервера не может быть позже времени формирования панели.");
            }

            uptimeSeconds = checked((long)uptime.TotalSeconds);
        }

        return new DashboardServerDto(
            server.Id,
            server.Name,
            server.InternalName,
            server.Description,
            server.Status,
            server.LastCheckedAtUtc,
            server.StartedAtUtc,
            uptimeSeconds,
            containers.Select(container => MapContainer(container, generatedAtUtc)).ToList());
    }

    private static DashboardContainerDto MapContainer(
        DashboardContainerReadRecord container,
        DateTimeOffset generatedAtUtc)
    {
        EnsureUtc(container.LastCheckedAtUtc, nameof(container.LastCheckedAtUtc));
        EnsureUtc(container.StartedAtUtc, nameof(container.StartedAtUtc));

        long? uptimeSeconds = null;
        if (container.StartedAtUtc is not null)
        {
            TimeSpan uptime = generatedAtUtc - container.StartedAtUtc.Value;
            if (uptime < TimeSpan.Zero)
            {
                throw new InvalidOperationException(
                    "Время запуска контейнера не может быть позже времени формирования панели.");
            }

            uptimeSeconds = checked((long)uptime.TotalSeconds);
        }

        return new DashboardContainerDto(
            container.Id,
            container.ServerId,
            GetDisplayName(container),
            container.Description,
            container.Status,
            container.LastCheckedAtUtc,
            container.StartedAtUtc,
            uptimeSeconds);
    }

    private static bool Matches(DashboardServerReadRecord server, string? search)
    {
        return search is null
            || server.Name.Contains(search, StringComparison.OrdinalIgnoreCase)
            || server.InternalName?.Contains(search, StringComparison.OrdinalIgnoreCase) == true
            || server.Description?.Contains(search, StringComparison.OrdinalIgnoreCase) == true;
    }

    private static bool Matches(DashboardContainerReadRecord container, string? search)
    {
        return search is null
            || GetDisplayName(container).Contains(search, StringComparison.OrdinalIgnoreCase)
            || container.Description?.Contains(search, StringComparison.OrdinalIgnoreCase) == true;
    }

    private static string GetDisplayName(DashboardContainerReadRecord container) =>
        container.LastKnownName ?? $"{MISSING_NAME} · {container.Id.ToString("N")[..8]}";

    private static void EnsureUtc(DateTimeOffset? value, string fieldName)
    {
        if (value is not null && value.Value.Offset != TimeSpan.Zero)
        {
            throw new InvalidOperationException($"Значение {fieldName} должно быть указано в UTC.");
        }
    }
}
