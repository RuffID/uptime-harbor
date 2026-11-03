namespace UptimeHarbor.Application.Cluster.Models;

/// <summary>Публичный ответ TLS origin до reserve; не устанавливает peer trust.</summary>
public record EnrollmentDiscovery(int Version, string Compatibility, string[] Capabilities,
    Guid ClusterId, long Generation, Guid InvitationId, string BrowserOrigin,
    string EnrollmentAddress, string PeerAddress, int MaxBytes, int TimeoutSeconds);
