using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using UptimeHarbor.Application.Common;
using UptimeHarbor.Application.ObjectAdministration;
using UptimeHarbor.Application.ObjectAdministration.Models;
using UptimeHarbor.Web.Controllers;
using UptimeHarbor.Web.Controllers.Models;
using Xunit;

namespace UptimeHarbor.Tests.Web;

public class AdminObjectsConcurrencyTests
{
    [Fact]
    public async Task UpdateServer_WhenApplicationReportsConcurrencyConflict_ReturnsHttp409()
    {
        AdminObjectsController controller = new(new ConflictAdministrationService());

        JsonResult result = await controller.UpdateServerAsync(
            Guid.NewGuid(),
            new ServerSettingsRequest
            {
                Name = "Server",
                EndpointUrl = "https://server.example/health",
                IsMonitoringEnabled = true,
                Version = 3
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(StatusCodes.Status409Conflict, result.StatusCode);
    }

    private class ConflictAdministrationService : IObjectAdministrationService
    {
        public Task<ServiceResult<AdminServerDto>> UpdateServerAsync(
            Guid serverId,
            string name,
            string endpointUrl,
            string? description,
            bool isMonitoringEnabled,
            long expectedVersion,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(ServiceResult<AdminServerDto>.Fail(
                ServiceErrorType.Conflict,
                "Объект был изменён другим процессом."));

        public Task<ServiceResult<IReadOnlyList<AdminServerDto>>> GetServersAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ServiceResult<IReadOnlyList<AdminContainerDto>>> GetContainersAsync(Guid serverId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ServiceResult<AdminServerDto>> CreateServerAsync(string name, string endpointUrl, string? description, bool isMonitoringEnabled, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ServiceResult<AdminContainerDto>> CreateContainerAsync(Guid serverId, string endpointUrl, string? description, bool isMonitoringEnabled, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ServiceResult<AdminContainerDto>> UpdateContainerAsync(Guid containerId, string endpointUrl, string? description, bool isMonitoringEnabled, long expectedVersion, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ServiceResult<ServerDeletionImpactDto>> GetServerDeletionImpactAsync(Guid serverId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ServiceResult> DeleteServerAsync(Guid serverId, int expectedContainerCount, long expectedVersion, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ServiceResult> DeleteContainerAsync(Guid containerId, long expectedVersion, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
