using UptimeHarbor.Application.Cluster.Models;

namespace UptimeHarbor.Web.Cluster;

/// <summary>Внутренний snapshot последнего аутентифицированного health, не браузерный DTO.</summary>
public record ObservedPeerHealth(PeerHealthSnapshot Health, TimeSpan Age);
