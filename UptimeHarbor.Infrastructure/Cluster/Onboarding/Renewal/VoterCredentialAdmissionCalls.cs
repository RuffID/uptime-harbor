using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Cluster.Onboarding.Renewal;

namespace UptimeHarbor.Infrastructure.Cluster.Onboarding.Renewal;

public sealed record VoterCredentialNonceCall(VoterCredentialRequest Request, VoterCredentialProof Process);

public sealed record VoterCredentialAuthorizeCall(RevisionCommand Command, VoterCredentialProofRequest ProofRequest);

public sealed record VoterCredentialBindCall(RevisionCommand Command, VoterCredentialSigningRequest Signing,
    EnrollmentIssuedArtifact Artifact, VoterCredentialProof BindProof);
