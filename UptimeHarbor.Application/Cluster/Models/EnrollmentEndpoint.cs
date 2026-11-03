namespace UptimeHarbor.Application.Cluster.Models;

/// <summary>Фиксированные endpoints преддоверенного протокола; не произвольный HTTP proxy.</summary>
public enum EnrollmentEndpoint
{
    Discovery,
    Nonce,
    Reserve,
    Issue,
    Admit,
    Resume,
    Renew,
    Probe,
    State
}
