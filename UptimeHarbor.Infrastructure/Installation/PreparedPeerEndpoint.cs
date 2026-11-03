namespace UptimeHarbor.Infrastructure.Installation;

/// <summary>Факт конфигурации отдельного listener до Build, не доказательство TLS-связи.</summary>
public record PreparedPeerEndpoint(bool Prepared, string Address);
