using System.Security.Cryptography.X509Certificates;
using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Cluster.Onboarding;
using UptimeHarbor.Application.Cluster.Onboarding.Renewal;
using UptimeHarbor.Domain.Cluster;
using UptimeHarbor.Infrastructure.Cluster.Onboarding;
using UptimeHarbor.Infrastructure.Cluster.Onboarding.Renewal;
using UptimeHarbor.Infrastructure.Cluster.Onboarding.Routing;
using UptimeHarbor.Infrastructure.Installation.Renewal.Models;

namespace UptimeHarbor.Infrastructure.Installation.Renewal;

/// <summary>Связь локальных pins с exact accepted Bind. Самостоятельно fresh majority не доказывает.</summary>
public static class LocalVoterCredentialBindingPolicy
{
    public static LocalVoterCredentialBindIntent Create(LocalVoterCredentialState local, Guid operationId)
    {
        LocalVoterCredentialAuthorization authorization = local.Authorization
            ?? throw new InvalidDataException("VoterCredentialLocalAuthorizationMissing");
        EnrollmentIssuedArtifact artifact = local.Artifact
            ?? throw new InvalidDataException("VoterCredentialLocalArtifactMissing");
        VoterCredentialRequest request = local.Intent.Request;
        RevisionCommand command = VoterCredentialBindCommand.Create(request, artifact, operationId);

        return new(request.OperationId, authorization.Rotation.AuthorizedIndex,
            authorization.RequestDigest, artifact.CertificateDigest, command);
    }

    public static void RequireIntent(LocalVoterCredentialState local, LocalVoterCredentialBindIntent intent)
    {
        LocalVoterCredentialBindIntent expected = Create(local, intent.Command.OperationId);
        if (intent != expected)
        {
            throw new InvalidDataException("VoterCredentialLocalBindIntentChanged");
        }
    }

    public static VoterCredentialRotation RequireProjection(LocalVoterCredentialState local,
        RevisionImage image, DateTimeOffset now)
    {
        LocalVoterCredentialAuthorization authorization = local.Authorization
            ?? throw new InvalidDataException("VoterCredentialLocalAuthorizationMissing");
        EnrollmentIssuedArtifact artifact = local.Artifact
            ?? throw new InvalidDataException("VoterCredentialLocalArtifactMissing");
        VoterCredentialRequest request = local.Intent.Request;
        EnrollmentIssuance leaf = request.Renewal.Issuance;
        OperationalNode node = EnrollmentPeerAuthority.RequireVoter(image, leaf.NodeId, leaf.StorageGeneration);
        AcceptedEnrollmentIssuer issuer = EnrollmentPeerAuthority.RequireIssuer(image, leaf.IssuerEpoch, now);
        VoterCredentialRotation? rotation = image.OnboardingContracts?.VoterCredentialRotations?.GetValueOrDefault(leaf.IssueId);
        if (image.ClusterId != request.ClusterId || image.Generation != request.Generation || node.Address != leaf.PeerUri
            || issuer != authorization.Issuer || rotation is null || rotation.Renewal != request.Renewal
            || rotation.AuthorizationOperationId != request.OperationId
            || rotation.AuthorizationDigest != authorization.Rotation.AuthorizationDigest
            || rotation.AuthorizedIndex != authorization.Rotation.AuthorizedIndex
            || rotation.AuthorizedAtUtc != authorization.Rotation.AuthorizedAtUtc || rotation.AuthorizedAtUtc > now)
        {
            throw new InvalidDataException("VoterCredentialLocalAcceptedAuthorizationChanged");
        }

        VoterCredentialSnapshot.Validate(image);
        RequireDecision(image, request.OperationId, rotation.AuthorizationDigest, rotation.AuthorizedIndex);
        LocalVoterCredentialMaterial.RequireAuthorization(local.Intent, authorization);
        LocalVoterCredentialMaterial.RequireArtifact(local.Intent, authorization, artifact);
        OperationalCredential previous = image.Operations.Credentials[request.Renewal.PreviousCredentialId];
        using X509Certificate2 old = X509CertificateLoader.LoadCertificate(Convert.FromBase64String(previous.PublicCertificate));
        if (EnrollmentCertificates.Hash(old.RawData) != request.Renewal.PreviousFingerprint
            || EnrollmentCertificates.Hash(old.PublicKey.ExportSubjectPublicKeyInfo()) != request.Renewal.PreviousSpkiDigest
            || new DateTimeOffset(old.NotAfter.ToUniversalTime()) != previous.ExpiresAtUtc)
        {
            throw new InvalidDataException("VoterCredentialLocalPreviousRegistryChanged");
        }

        return rotation;
    }

    public static LocalVoterCredentialBinding RequireBound(LocalVoterCredentialState local,
        LocalVoterCredentialBindIntent intent, RevisionImage image, DateTimeOffset now)
    {
        RequireIntent(local, intent);
        VoterCredentialRotation rotation = RequireProjection(local, image, now);
        string digest = RevisionCodec.Digest(intent.Command);
        OperationalCredential expected = intent.Command.EnrollmentTrust!.Rotation!.Credential!;
        OperationalCredential? credential = image.Operations.Credentials.GetValueOrDefault(expected.Id);
        VoterCredentialRotation? latest = VoterCredentialPolicy.Latest(image, expected.NodeId);
        if (rotation.CredentialRevision <= 0 || rotation.BindOperationId != intent.Command.OperationId
            || rotation.BindDigest != digest || rotation.BoundAtUtc > now
            || latest?.CredentialRevision != rotation.CredentialRevision || credential != expected
            || !VoterCredentialPolicy.IsUsable(image, expected, now))
        {
            throw new InvalidDataException("VoterCredentialLocalExactBindRequired");
        }

        using X509Certificate2 leaf = X509CertificateLoader.LoadCertificate(local.Artifact!.CertificateDer);
        if (!VoterCredentialLeafTrust.ValidateDedicated(image, expected, leaf, now, server: false))
        {
            throw new InvalidDataException("VoterCredentialLocalBoundLeafUnavailable");
        }

        CommandResult result = RequireDecision(image, intent.Command.OperationId, digest, rotation.CredentialRevision);
        return new(EnrollmentCertificates.Hash(StrictOnboardingJson.Encode(intent)), result,
            rotation.BoundAtUtc!.Value, rotation.OverlapUntilUtc!.Value);
    }

    private static CommandResult RequireDecision(RevisionImage image, Guid operationId, string digest, long index)
    {
        CommandResult? result = image.Results.GetValueOrDefault(operationId);
        if (index < 1 || index > image.Index || result is null || result.OperationId != operationId
            || result.Digest != digest || result.Outcome != OperationOutcome.Committed || result.Index != index
            || result.AppliedRevision != index || result.Term < 1 || result.Term > image.Term
            || result.Reason is not null || result.ConflictId.HasValue || result.ReplacedBy.HasValue
            || result.ActorRevision != 0 || result.SubjectUserId.HasValue)
        {
            throw new InvalidDataException("VoterCredentialLocalExactDecisionRequired");
        }

        return result;
    }
}
