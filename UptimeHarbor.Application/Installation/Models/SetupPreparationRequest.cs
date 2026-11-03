namespace UptimeHarbor.Application.Installation;

public record SetupPreparationRequest(string Mode, long ExpectedRevision, long ExpectedGeneration,
    string? CommonLogin = null, string? CommonPassword = null)
{
    public override string ToString()
    {
        return "SetupPreparationRequest";
    }
}
