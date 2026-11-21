using UptimeHarbor.Application.Cluster.Models;

namespace UptimeHarbor.Infrastructure.Installation.Renewal.Models;

internal record LocalVoterCredentialManifest(int Version, string MaterialReference, string IntentMetadataDigest,
    string AuthorizationPinDigest, string CsrDigest, string SpkiDigest, string TemplateDigest, string CertificateFingerprint,
    string BindingDigest, Guid IssueId, long CredentialRevision, ProjectionHead AcceptedHead);
