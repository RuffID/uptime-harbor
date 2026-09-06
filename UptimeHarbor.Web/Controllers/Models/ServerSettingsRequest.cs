namespace UptimeHarbor.Web.Controllers.Models;

public class ServerSettingsRequest
{
    public string? Name { get; init; }

    public string? EndpointUrl { get; init; }

    public string? Description { get; init; }

    public bool IsMonitoringEnabled { get; init; }

    public long? Version { get; init; }
}
