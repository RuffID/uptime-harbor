using Microsoft.AspNetCore.Mvc;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using UptimeHarbor.Application.Dashboard.Models;
using UptimeHarbor.Application.ObjectAdministration.Models;
using UptimeHarbor.Web.Controllers;
using UptimeHarbor.Domain.Monitoring;
using Xunit;

namespace UptimeHarbor.Tests.Web;

public class ReadContractSecurityTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void DashboardServer_UsesUtcBootTimeAndNullableUptimeInMvcJson(bool hasBootTime)
    {
        // Arrange
        ServiceCollection services = new();
        services.AddControllers();
        using ServiceProvider provider = services.BuildServiceProvider();
        JsonSerializerOptions options = provider.GetRequiredService<IOptions<JsonOptions>>().Value.JsonSerializerOptions;
        DateTimeOffset? startedAtUtc = null;
        long? uptimeSeconds = null;
        if (hasBootTime)
        {
            startedAtUtc = new DateTimeOffset(2026, 8, 25, 8, 0, 0, TimeSpan.Zero);
            uptimeSeconds = 7_200;
        }

        DashboardServerDto server = new(Guid.NewGuid(), "Server", "ubuntu", null,
            MonitoringStatus.Available, null, startedAtUtc, uptimeSeconds, []);

        // Act
        using JsonDocument json = JsonDocument.Parse(JsonSerializer.Serialize(server, options));
        JsonElement payload = json.RootElement;

        // Assert
        if (hasBootTime)
        {
            Assert.Equal(startedAtUtc, payload.GetProperty("startedAtUtc").GetDateTimeOffset());
            Assert.EndsWith("+00:00", payload.GetProperty("startedAtUtc").GetString());
            Assert.Equal(7_200, payload.GetProperty("uptimeSeconds").GetInt64());
        }
        else
        {
            Assert.Equal(JsonValueKind.Null, payload.GetProperty("startedAtUtc").ValueKind);
            Assert.Equal(JsonValueKind.Null, payload.GetProperty("uptimeSeconds").ValueKind);
        }

        Assert.False(payload.TryGetProperty("endpointUrl", out _));
    }

    [Fact]
    public void DashboardReadDtos_DoNotExposeAdministrativeEndpointFields()
    {
        Assert.Null(typeof(DashboardServerDto).GetProperty("EndpointUrl"));
        Assert.Null(typeof(DashboardContainerDto).GetProperty("EndpointUrl"));
        Assert.Null(typeof(DashboardServerDto).GetProperty("IsMonitoringEnabled"));
        Assert.Null(typeof(DashboardContainerDto).GetProperty("IsMonitoringEnabled"));
        Assert.NotNull(typeof(DashboardServerDto).GetProperty("Name"));
        Assert.NotNull(typeof(DashboardServerDto).GetProperty("InternalName"));
    }

    [Fact]
    public void AdministrativeDtos_KeepEndpointNeededForEditing()
    {
        Assert.NotNull(typeof(AdminServerDto).GetProperty("EndpointUrl"));
        Assert.NotNull(typeof(AdminContainerDto).GetProperty("EndpointUrl"));
    }

    [Fact]
    public void PublicObjectsReadRoute_IsNotRegisteredByAnyController()
    {
        IReadOnlyList<string> routes = typeof(DashboardController).Assembly
            .GetTypes()
            .Where(type => typeof(ControllerBase).IsAssignableFrom(type))
            .SelectMany(type => type.GetCustomAttributes(typeof(RouteAttribute), inherit: true))
            .Cast<RouteAttribute>()
            .Select(attribute => attribute.Template)
            .Where(template => template is not null)
            .Cast<string>()
            .ToList();

        Assert.DoesNotContain(
            routes,
            route => string.Equals(
                route.Trim('/'),
                "api/objects",
                StringComparison.OrdinalIgnoreCase));
    }
}
