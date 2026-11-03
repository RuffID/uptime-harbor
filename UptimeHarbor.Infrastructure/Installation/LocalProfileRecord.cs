namespace UptimeHarbor.Infrastructure.Installation;

public class LocalProfileRecord
{
    public int Id { get; set; } = 1;
    public Guid InstallIdentity { get; set; }
    public long Revision { get; set; }
    public string Payload { get; set; } = string.Empty;
}
