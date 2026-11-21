using UptimeHarbor.Application.Cluster.Models;

namespace UptimeHarbor.Infrastructure.Installation.Renewal.Models;

internal record LocalVoterCredentialConfirmation(int Version, string ReferenceDigest, ProjectionHead AcceptedHead);
