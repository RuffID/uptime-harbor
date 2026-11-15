namespace UptimeHarbor.Application.Cluster.Models;

/// <summary>Только принятый leaf template и public CSR; private material не передаётся.</summary>
public record EnrollmentSigningRequest(int Version, EnrollmentRequest Request, EnrollmentIssuance Issuance,
    long AuthorizationIndex, string CsrDer);
