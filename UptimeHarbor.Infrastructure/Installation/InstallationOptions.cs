namespace UptimeHarbor.Infrastructure.Installation;

public class InstallationOptions
{
    public const string SECTION_NAME = "Installation";
    public string InstallationIntent { get; set; } = "Legacy";
    public string StateDirectory { get; set; } = string.Empty;
    public string SecretDirectory { get; set; } = string.Empty;
    public string KeyDirectory { get; set; } = string.Empty;
    public string BrowserOrigin { get; set; } = string.Empty;
    public string PeerEndpoint { get; set; } = string.Empty;
    public string[] AllowedPeerNetworks { get; set; } = [];
    public bool Setup => InstallationIntent == "Setup";
}
