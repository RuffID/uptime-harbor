namespace UptimeHarbor.Application.Cluster.Models;

/// <summary>Аутентифицированный ответ о точной попытке после majority barrier, включая доказанное отсутствие reserve.</summary>
public record EnrollmentState(int Version, EnrollmentRequest Request, EnrollmentProof ResponseProof,
    EnrollmentDescriptor? Descriptor);
