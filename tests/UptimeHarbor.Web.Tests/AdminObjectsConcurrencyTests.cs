using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using System.Security.Claims;
using UptimeHarbor.Domain.Monitoring;
using UptimeHarbor.Application.Common;
using UptimeHarbor.Application.ObjectAdministration;
using UptimeHarbor.Application.ObjectAdministration.Models;
using UptimeHarbor.Web.Controllers;
using UptimeHarbor.Web.Controllers.Models;
using Xunit;

namespace UptimeHarbor.Tests.Web;

public class AdminObjectsConcurrencyTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task UpdateServer_LogsActualOutcomeAndTrustedActorWithoutSettings(bool success)
    {
        // Arrange
        AuditLogger logger = new();
        string actorId = Guid.NewGuid().ToString();
        AdminObjectsController controller = new(new ConflictAdministrationService(success), logger)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity([
                        new Claim(ClaimTypes.NameIdentifier, actorId),
                        new Claim(ClaimTypes.Name, "private-login")], "test"))
                }
            }
        };

        // Act
        await controller.UpdateServerAsync(Guid.NewGuid(), new ServerSettingsRequest
        {
            Name = "private-name", EndpointUrl = "https://private-endpoint.invalid/health",
            Description = "private-description", IsMonitoringEnabled = true, Version = 3
        }, TestContext.Current.CancellationToken);

        // Assert
        string message = Assert.Single(logger.Messages);
        Assert.Contains(actorId, message, StringComparison.Ordinal);
        Assert.DoesNotContain("private-", message, StringComparison.Ordinal);
        if (success)
        {
            Assert.Contains("saved", message, StringComparison.Ordinal);
        }
        else
        {
            Assert.Contains("refused", message, StringComparison.Ordinal);
            Assert.DoesNotContain("saved", message, StringComparison.Ordinal);
        }
    }

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

    private class ConflictAdministrationService(bool success = false) : IObjectAdministrationService
    {
        public Task<ServiceResult<AdminServerDto>> UpdateServerAsync(
            Guid serverId,
            string name,
            string endpointUrl,
            string? description,
            bool isMonitoringEnabled,
            long expectedVersion,
            CancellationToken cancellationToken = default,
            IReadOnlyList<ContainerLinkUpdate>? containerLinks = null)
        {
            if (success)
            {
                return Task.FromResult(ServiceResult<AdminServerDto>.Ok(new(serverId, name,
                    endpointUrl, description, isMonitoringEnabled, null, MonitoringStatus.Unknown, 0, expectedVersion + 1)));
            }

            return Task.FromResult(ServiceResult<AdminServerDto>.Fail(
                ServiceErrorType.Conflict,
                "Объект был изменён другим процессом."));
        }

        public Task<ServiceResult<IReadOnlyList<AdminServerDto>>> GetServersAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ServiceResult<IReadOnlyList<AdminContainerDto>>> GetContainersAsync(
            Guid? serverId = null,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
        public Task<ServiceResult<AdminServerDto>> CreateServerAsync(string name, string endpointUrl, string? description, bool isMonitoringEnabled, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ServiceResult<AdminContainerDto>> CreateContainerAsync(
            Guid? serverId,
            string endpointUrl,
            string? description,
            bool isMonitoringEnabled,
            CancellationToken cancellationToken = default,
            string? name = null)
        {
            throw new NotSupportedException();
        }
        public Task<ServiceResult<AdminContainerDto>> UpdateContainerAsync(
            Guid containerId,
            string endpointUrl,
            string? description,
            bool isMonitoringEnabled,
            long expectedVersion,
            CancellationToken cancellationToken = default,
            ContainerServerAssignment? serverAssignment = null,
            string? name = null)
        {
            throw new NotSupportedException();
        }
        public Task<ServiceResult<ServerDeletionImpactDto>> GetServerDeletionImpactAsync(Guid serverId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ServiceResult> DeleteServerAsync(Guid serverId, int expectedContainerCount, long expectedVersion, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ServiceResult> DeleteContainerAsync(Guid containerId, long expectedVersion, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private class AuditLogger : ILogger<AdminObjectsController>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull
        {
            return null;
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return true;
        }

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Messages.Add(formatter(state, exception));
        }
    }
}
