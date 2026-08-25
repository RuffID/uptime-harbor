namespace UptimeHarbor.Web.Controllers.Models;

public class ObjectSettingsRequest
{
    public string? EndpointUrl { get; init; }

    public string? Description { get; init; }

    public bool IsMonitoringEnabled { get; init; }
}
