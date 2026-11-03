namespace UptimeHarbor.Infrastructure.Installation;

public class LocalStepRecord
{
    public Guid Id { get; set; }
    public Guid AttemptId { get; set; }
    public string Payload { get; set; } = string.Empty;
}
