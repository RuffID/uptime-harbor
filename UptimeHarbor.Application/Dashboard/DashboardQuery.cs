using UptimeHarbor.Application.Abstractions.UnitOfWork;
using UptimeHarbor.Application.Common;
using UptimeHarbor.Application.Dashboard.Models;
using UptimeHarbor.Application.Security;
using UptimeHarbor.Application.Visibility;
using UptimeHarbor.Domain.Access;
using UptimeHarbor.Domain.Monitoring;

namespace UptimeHarbor.Application.Dashboard;

public class DashboardQuery(
    IUserAccessContext accessContext,
    IVisibilitySettingsUnitOfWork unitOfWork,
    TimeProvider timeProvider)
    : IDashboardQuery
{
    private const string MISSING_NAME = "Имя не получено";

    public async Task<ServiceResult<DashboardSnapshotDto>> GetSnapshotAsync(
        string? search,
        CancellationToken cancellationToken = default)
    {
        ServiceError? accessError = GetAccessError();
        if (accessError is not null)
        {
            return ServiceResult<DashboardSnapshotDto>.Fail(accessError);
        }

        List<MonitoredServer> allServers =
            await unitOfWork.MonitoredServers.GetAllAsync(cancellationToken);
        List<MonitoredContainer> allContainers =
            await unitOfWork.MonitoredContainers.GetAllAsync(cancellationToken);

        HashSet<Guid> permittedServerIds = [];
        HashSet<Guid> permittedContainerIds = [];
        if (!accessContext.IsAdministrator)
        {
            List<ServerVisibilityPermission> serverPermissions =
                await unitOfWork.VisibilityPermissions.GetServerPermissionsAsync(
                    accessContext.UserId,
                    cancellationToken);
            List<ContainerVisibilityPermission> containerPermissions =
                await unitOfWork.VisibilityPermissions.GetContainerPermissionsAsync(
                    accessContext.UserId,
                    cancellationToken);
            permittedServerIds = serverPermissions
                .Select(permission => permission.ServerId)
                .ToHashSet();
            permittedContainerIds = containerPermissions
                .Select(permission => permission.ContainerId)
                .ToHashSet();
        }

        List<MonitoredServer> accessibleServers = allServers
            .Where(server => ObjectVisibilityPolicy.CanAccessServer(
                accessContext.IsAdministrator,
                server.Id,
                permittedServerIds))
            .ToList();
        List<MonitoredContainer> accessibleContainers = allContainers
            .Where(container => ObjectVisibilityPolicy.CanAccessContainer(
                accessContext.IsAdministrator,
                container.ServerId,
                container.Id,
                permittedServerIds,
                permittedContainerIds))
            .ToList();

        string? normalizedSearch = string.IsNullOrWhiteSpace(search) ? null : search.Trim();
        DateTimeOffset generatedAtUtc = timeProvider.GetUtcNow();
        EnsureUtc(generatedAtUtc, nameof(generatedAtUtc));

        List<DashboardServerDto> servers = [];
        foreach (MonitoredServer server in accessibleServers.OrderBy(GetDisplayName))
        {
            bool serverMatches = Matches(server, normalizedSearch);
            IEnumerable<MonitoredContainer> serverContainers = accessibleContainers
                .Where(container => container.ServerId == server.Id);

            if (normalizedSearch is not null && !serverMatches)
            {
                serverContainers = serverContainers.Where(container => Matches(container, normalizedSearch));
            }

            List<MonitoredContainer> matchingContainers = serverContainers
                .OrderBy(GetDisplayName)
                .ToList();
            if (normalizedSearch is not null && !serverMatches && matchingContainers.Count == 0)
            {
                continue;
            }

            servers.Add(MapServer(server, matchingContainers, generatedAtUtc));
        }

        return ServiceResult<DashboardSnapshotDto>.Ok(
            new DashboardSnapshotDto(generatedAtUtc, servers));
    }

    private ServiceError? GetAccessError()
    {
        if (!accessContext.IsAuthenticated)
        {
            return new ServiceError(ServiceErrorType.Unauthorized, "Требуется аутентификация.");
        }

        return !accessContext.IsEnabled
            ? new ServiceError(ServiceErrorType.Forbidden, "Пользователь заблокирован.")
            : null;
    }

    private static DashboardServerDto MapServer(
        MonitoredServer server,
        IReadOnlyList<MonitoredContainer> containers,
        DateTimeOffset generatedAtUtc)
    {
        EnsureUtc(server.LastCheckedAtUtc, nameof(server.LastCheckedAtUtc));
        return new DashboardServerDto(
            server.Id,
            GetDisplayName(server),
            server.Description,
            server.Status,
            server.LastCheckedAtUtc,
            containers.Select(container => MapContainer(container, generatedAtUtc)).ToList());
    }

    private static DashboardContainerDto MapContainer(
        MonitoredContainer container,
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

    private static bool Matches(MonitoredObject monitoredObject, string? search)
    {
        return search is null
            || GetDisplayName(monitoredObject).Contains(search, StringComparison.OrdinalIgnoreCase)
            || monitoredObject.Description?.Contains(search, StringComparison.OrdinalIgnoreCase) == true;
    }

    private static string GetDisplayName(MonitoredObject monitoredObject)
    {
        return monitoredObject.LastKnownName
            ?? $"{MISSING_NAME} · {monitoredObject.Id.ToString("N")[..8]}";
    }

    private static void EnsureUtc(DateTimeOffset? value, string fieldName)
    {
        if (value is not null && value.Value.Offset != TimeSpan.Zero)
        {
            throw new InvalidOperationException($"Значение {fieldName} должно быть указано в UTC.");
        }
    }
}
