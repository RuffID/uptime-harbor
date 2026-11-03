namespace UptimeHarbor.Infrastructure.Installation;

public class LocalAttemptRecord
{
    public Guid Id { get; set; }
    public long Revision { get; set; }
    public string Payload { get; set; } = string.Empty;
}
