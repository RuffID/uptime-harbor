namespace UptimeHarbor.Web.Configuration;

public class BootstrapAdminOptions
{
    public const string SECTION_NAME = "BootstrapAdmin";

    public string Login { get; init; } = string.Empty;

    public string Password { get; init; } = string.Empty;
}
