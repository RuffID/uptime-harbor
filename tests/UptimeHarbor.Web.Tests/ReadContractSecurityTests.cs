using Microsoft.AspNetCore.Mvc;
using UptimeHarbor.Application.Dashboard.Models;
using UptimeHarbor.Application.ObjectAdministration.Models;
using UptimeHarbor.Web.Controllers;
using Xunit;

namespace UptimeHarbor.Tests.Web;

public class ReadContractSecurityTests
{
    [Fact]
    public void DashboardReadDtos_DoNotExposeAdministrativeEndpointFields()
    {
        Assert.Null(typeof(DashboardServerDto).GetProperty("EndpointUrl"));
        Assert.Null(typeof(DashboardContainerDto).GetProperty("EndpointUrl"));
        Assert.Null(typeof(DashboardServerDto).GetProperty("IsMonitoringEnabled"));
        Assert.Null(typeof(DashboardContainerDto).GetProperty("IsMonitoringEnabled"));
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
