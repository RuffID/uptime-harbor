using System.Text.Json.Serialization;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Cluster.Onboarding.Renewal;

namespace UptimeHarbor.Infrastructure.Cluster.Onboarding.Renewal;

public static class VoterContinuationWire
{
    public const string ROOT = "/api/credential-continuation/v1";

    public sealed record Challenge(VoterCredentialProofRequest Call, string Purpose, string? AuthorizationDigest,
        string? RegistrationDigest = null, string? CertificateDigest = null);
    public sealed record Registration(VoterCredentialProofRequest Call, string AuthorizationDigest);
    public sealed record Issuance(VoterCredentialProofRequest Call, string RegistrationDigest);
    public sealed record IssuedLeaf(Guid OperationId, Guid IssueId, byte[] CertificateDer, string CertificateDigest);
    public sealed record Binding(VoterCredentialProofRequest Call, string RegistrationDigest, string CertificateDigest);
    public sealed record BoundLeaf(Guid OperationId, Guid IssueId, CommandResult Decision, long CredentialRevision);
    public sealed record Nonce(string Value);
    public sealed record SliceQuery(VoterCredentialProofRequest Call, ProjectionHead Replay, long NativeTerm, string VotersDigest)
    {
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public BoundDiscovery? Discovery { get; init; }
    }

    public sealed record BoundDiscovery(string RegistrationDigest, string CertificateDigest);
    public sealed record Stage(RevisionCommand Command, CommandResult Decision);
    public sealed record BoundHistory(Stage Requested, Stage Authorized, Stage Registered, Stage Bound);
    public sealed record AcceptedSlice(string QueryDigest, ProjectionHead Head, long Term, long RecoveryVersion,
        long MembershipVersion, OperationalNode Subject, OperationalNode Audience, OperationalCredential Previous,
        AcceptedEnrollmentIssuer PreviousIssuer, AcceptedEnrollmentIssuer Issuer, OnlineProcessLease? Process,
        Stage? Requested, Stage? Authorized, Stage? Registered, Stage? Bound)
    {
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public BoundHistory? History { get; init; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public Stage? ProcessRegistration { get; init; }

        [JsonIgnore]
        internal Stage? CredentialBinding => History?.Bound ?? Bound;

        [JsonIgnore]
        internal Stage? IssuingRegistration => History?.Registered ?? Registered;
    }
}
