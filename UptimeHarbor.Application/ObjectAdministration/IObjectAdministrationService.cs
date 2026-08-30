using UptimeHarbor.Application.Common;
using UptimeHarbor.Application.ObjectAdministration.Models;

namespace UptimeHarbor.Application.ObjectAdministration;

public interface IObjectAdministrationService
{
    Task<ServiceResult<IReadOnlyList<AdminServerDto>>> GetServersAsync(
        CancellationToken cancellationToken = default);

    Task<ServiceResult<IReadOnlyList<AdminContainerDto>>> GetContainersAsync(
        Guid serverId,
        CancellationToken cancellationToken = default);

    Task<ServiceResult<AdminServerDto>> CreateServerAsync(
        string endpointUrl,
        string? description,
        bool isMonitoringEnabled,
        CancellationToken cancellationToken = default);

    Task<ServiceResult<AdminServerDto>> UpdateServerAsync(
        Guid serverId,
        string endpointUrl,
        string? description,
        bool isMonitoringEnabled,
        long expectedVersion,
        CancellationToken cancellationToken = default);

    Task<ServiceResult<AdminContainerDto>> CreateContainerAsync(
        Guid serverId,
        string endpointUrl,
        string? description,
        bool isMonitoringEnabled,
        CancellationToken cancellationToken = default);

    Task<ServiceResult<AdminContainerDto>> UpdateContainerAsync(
        Guid containerId,
        string endpointUrl,
        string? description,
        bool isMonitoringEnabled,
        long expectedVersion,
        CancellationToken cancellationToken = default);

    Task<ServiceResult<ServerDeletionImpactDto>> GetServerDeletionImpactAsync(
        Guid serverId,
        CancellationToken cancellationToken = default);

    Task<ServiceResult> DeleteServerAsync(
        Guid serverId,
        int expectedContainerCount,
        long expectedVersion,
        CancellationToken cancellationToken = default);

    Task<ServiceResult> DeleteContainerAsync(
        Guid containerId,
        long expectedVersion,
        CancellationToken cancellationToken = default);
}
