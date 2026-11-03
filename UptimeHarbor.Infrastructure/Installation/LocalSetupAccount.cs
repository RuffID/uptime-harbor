namespace UptimeHarbor.Infrastructure.Installation;

/// <summary>Локальная Identity V3 credential, не ApplicationUser.</summary>
public class LocalSetupAccount
{
    public Guid Id { get; set; }
    public Guid CreatedForInstallIdentity { get; set; }
    public string NormalizedLogin { get; set; } = string.Empty;
    public string? PasswordHash { get; set; }
    public string Stamp { get; set; } = string.Empty;
    public bool Disabled { get; set; }
    public int FailedCount { get; set; }
    public DateTimeOffset? LockoutUntilUtc { get; set; }
    public long Revision { get; set; }
    public override string ToString()
    {
        return $"LocalSetupAccount {Id}";
    }
}
