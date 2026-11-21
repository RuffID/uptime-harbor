using UptimeHarbor.Application.Cluster.Models;

namespace UptimeHarbor.Infrastructure.Installation.Renewal.Models;

internal record LocalVoterCredentialInstalledReference(int Version, string ManifestDigest, Guid IssueId,
    long CredentialRevision, ProjectionHead AcceptedHead);
