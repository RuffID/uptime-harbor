namespace UptimeHarbor.Web.Controllers.Models;

public class DeleteServerRequest
{
    public int ExpectedContainerCount { get; init; }

    public long Version { get; init; }
}
