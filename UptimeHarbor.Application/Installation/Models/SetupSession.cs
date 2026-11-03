namespace UptimeHarbor.Application.Installation;

public record SetupSession(Guid AccountId, Guid InstallIdentity, string Stamp, long Epoch,
    Guid BootId, DateTimeOffset StartedAtUtc)
{
    public override string ToString()
    {
        return "SetupSession";
    }
}
