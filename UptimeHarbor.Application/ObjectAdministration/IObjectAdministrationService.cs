using UptimeHarbor.Application.Common;
using UptimeHarbor.Application.ObjectAdministration.Models;

namespace UptimeHarbor.Application.ObjectAdministration;

public interface IObjectAdministrationService
{
    Task<ServiceResult<IReadOnlyList<AdminServerDto>>> GetServersAsync(
        CancellationToken cancellationToken = default);

    Task<ServiceResult<IReadOnlyList<AdminContainerDto>>> GetContainersAsync(
        Guid? serverId = null,
        CancellationToken cancellationToken = default);

    Task<ServiceResult<AdminServerDto>> CreateServerAsync(
        string name,
        string endpointUrl,
        string? description,
        bool isMonitoringEnabled,
        CancellationToken cancellationToken = default);

    Task<ServiceResult<AdminServerDto>> UpdateServerAsync(
        Guid serverId,
        string name,
        string endpointUrl,
        string? description,
        bool isMonitoringEnabled,
        long expectedVersion,
        CancellationToken cancellationToken = default,
        IReadOnlyList<ContainerLinkUpdate>? containerLinks = null);

    Task<ServiceResult<AdminContainerDto>> CreateContainerAsync(
        Guid? serverId,
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
        CancellationToken cancellationToken = default,
        ContainerServerAssignment? serverAssignment = null);

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
