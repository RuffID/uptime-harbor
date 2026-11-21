using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Cluster.Onboarding;
using UptimeHarbor.Application.Cluster.Onboarding.Renewal;
using UptimeHarbor.Domain.Cluster;
using UptimeHarbor.Infrastructure.Installation;
using UptimeHarbor.Infrastructure.Installation.Renewal;

namespace UptimeHarbor.Infrastructure.Cluster.Onboarding.Renewal;

public sealed record VoterCredentialRetirementIntent(RevisionCommand Command, VoterCredentialHandoff Handoff)
{
    internal static VoterCredentialRetirementIntent Create(RevisionImage image, VoterCredentialRotation rotation,
        VoterCredentialHandoff handoff, Guid id)
    {
        string proof = EnrollmentCertificates.Hash(StrictOnboardingJson.Encode(handoff));
        VoterCredentialRequest request = new(1, image.ClusterId, image.Generation, rotation.AuthorizationOperationId, rotation.Renewal);
        RevisionCommand original = VoterCredentialAuthorizationCommand.Create(request);
        RevisionCommand command = original with
        {
            Action = "RetireOldCredential", OperationId = id,
            EnrollmentTrust = original.EnrollmentTrust! with { Rotation = new(rotation.Renewal, null, proof) }
        };
        RevisionCodec.Validate(command);
        return new(command, handoff);
    }

    public VoterCredentialRotation Require(RevisionImage image, DateTimeOffset now)
    {
        return Require(image, now, out _);
    }

    public CommandResult? ReadDecision(RevisionImage image, DateTimeOffset now)
    {
        Require(image, now, out CommandResult? decision);
        return decision;
    }

    private VoterCredentialRotation Require(RevisionImage image, DateTimeOffset now, out CommandResult? decision)
    {
        if (Command is not { Action: "RetireOldCredential", EnrollmentTrust.Rotation.Renewal.Issuance: { } subject }
            || Handoff is null
            || image.Operations.MaintenanceId != Guid.Empty)
        {
            throw new UnauthorizedAccessException("VoterCredentialRetirementIntentInvalid");
        }

        VoterCredentialRotation rotation = Handoff.RequireSubject(image, subject.NodeId, subject.StorageGeneration, now,
            allowRetired: true);
        var request = new VoterCredentialRequest(1, image.ClusterId, image.Generation, rotation.AuthorizationOperationId, rotation.Renewal);
        RevisionCommand bind = VoterCredentialBindCommand.CreateFromCredential(request, image.Operations.Credentials[Handoff.IssueId],
            rotation.BindOperationId!.Value);
        if (Command != Create(image, rotation, Handoff, Command.OperationId).Command
            || RevisionCodec.Digest(bind) != Handoff.BindDigest
            || image.Operations.Credentials.GetValueOrDefault(rotation.Renewal.PreviousCredentialId) is not { } previous
            || previous.NodeId != subject.NodeId || previous.Fingerprint != rotation.Renewal.PreviousFingerprint
            || rotation.OverlapUntilUtc != VoterCredentialPolicy.OverlapUntil(rotation.BoundAtUtc!.Value, previous.ExpiresAtUtc)
            || now < rotation.OverlapUntilUtc)
        {
            throw new UnauthorizedAccessException("VoterCredentialRetirementIntentChanged");
        }

        using X509Certificate2 old = X509CertificateLoader.LoadCertificate(Convert.FromBase64String(previous.PublicCertificate));
        if (EnrollmentCertificates.Hash(old.RawData) != previous.Fingerprint
            || EnrollmentCertificates.Hash(old.PublicKey.ExportSubjectPublicKeyInfo()) != rotation.Renewal.PreviousSpkiDigest)
        {
            throw new UnauthorizedAccessException("VoterCredentialRetirementPreviousLeafChanged");
        }

        decision = CheckDecision(image, rotation, Command);
        return rotation;
    }

    internal static CommandResult? CheckDecision(RevisionImage image, VoterCredentialRotation rotation, RevisionCommand command)
    {
        CommandResult? result = RevisionDispatcher.ReadVoterCredentialDecision(image, command);
        if (result is null && !rotation.RetiredIndex.HasValue)
        {
            return null;
        }

        if (result?.Outcome != OperationOutcome.Committed || result.Index != rotation.RetiredIndex
            || rotation.RetireOperationId != command.OperationId || rotation.RetireDigest != result.Digest
            || rotation.InstallationProofDigest != command.EnrollmentTrust!.Rotation!.InstallationProofDigest
            || rotation.RetiredAtUtc is not { } retiredAt || retiredAt < rotation.OverlapUntilUtc)
        {
            throw new InvalidDataException("VoterCredentialRetirementDecisionChanged");
        }

        return result;
    }
}

public sealed record VoterCredentialRetirementEvidence(Guid NodeId, Guid StorageGeneration, Guid CredentialId,
    string Fingerprint, long Term, long MembershipVersion, DateTimeOffset ConfirmedAtUtc, string Signature)
{
    public static void RequireMembership(RevisionImage image, ConsensusStatus status)
    {
        var voters = image.Operations.Nodes.Values.Where(node => node.Voting && !node.Removed).Select(node => node.NodeId);
        if (status.ClusterId != image.ClusterId || status.Generation != image.Generation || status.Term < image.Term
            || status.MembershipVersion != image.Operations.MembershipVersion || status.CommittedIndex > image.Index
            || status.ConsensusToken.IsCancellationRequested || !status.VotingNodes.SetEquals(voters))
        {
            throw new UnauthorizedAccessException("VoterCredentialRetirementMembershipChanged");
        }
    }

    public VoterCredentialRetirementEvidence Sign(VoterCredentialRetirementIntent intent, X509Certificate2 certificate)
    {
        if (Fingerprint != EnrollmentCertificates.Hash(certificate.RawData))
        {
            throw new UnauthorizedAccessException("VoterCredentialRetirementWitnessKeyChanged");
        }

        byte[] data = Canonical(intent);
        using ECDsa? ec = certificate.GetECDsaPrivateKey();
        using RSA? rsa = certificate.GetRSAPrivateKey();
        byte[] signature = ec?.SignData(data, HashAlgorithmName.SHA256)
            ?? rsa?.SignData(data, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
            ?? throw new UnauthorizedAccessException("VoterCredentialRetirementWitnessKeyUnavailable");
        return this with { Signature = Convert.ToBase64String(signature) };
    }

    public void Require(VoterCredentialRetirementIntent intent, RevisionImage image, ConsensusStatus status, DateTimeOffset now)
    {
        RequireMembership(image, status);
        if (status.VotingNodes.Count < 2 || !status.VotingNodes.Contains(NodeId) || NodeId == intent.Command.OriginNodeId
            || Term != status.Term || MembershipVersion != status.MembershipVersion || ConfirmedAtUtc.Offset != TimeSpan.Zero
            || ConfirmedAtUtc < now.AddSeconds(-30) || ConfirmedAtUtc > now.AddSeconds(5)
            || Signature is not { Length: > 0 and <= 1024 })
        {
            throw new UnauthorizedAccessException("VoterCredentialRetirementWitnessChanged");
        }

        OperationalNode? witness = image.Operations.Nodes.GetValueOrDefault(NodeId);
        if (witness is null || witness.StorageGeneration != StorageGeneration)
        {
            throw new UnauthorizedAccessException("VoterCredentialRetirementWitnessChanged");
        }

        VoterCredentialHandoff.RequireLeaf(image, witness, CredentialId, Fingerprint, now);
        OperationalCredential credential = image.Operations.Credentials[CredentialId];
        using X509Certificate2 leaf = X509CertificateLoader.LoadCertificate(Convert.FromBase64String(credential.PublicCertificate));
        using ECDsa? ec = leaf.GetECDsaPublicKey();
        using RSA? rsa = leaf.GetRSAPublicKey();
        byte[] data = Canonical(intent);
        byte[] signature = Convert.FromBase64String(Signature);
        if (EnrollmentCertificates.Hash(leaf.RawData) != Fingerprint
            || (ec?.VerifyData(data, signature, HashAlgorithmName.SHA256)
                ?? rsa?.VerifyData(data, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)) != true)
        {
            throw new UnauthorizedAccessException("VoterCredentialRetirementWitnessSignatureInvalid");
        }
    }

    private byte[] Canonical(VoterCredentialRetirementIntent intent)
    {
        return StrictOnboardingJson.Encode(new
        {
            Domain = "UptimeHarbor.VoterCredentialRetirementWitness.v1",
            Operation = intent.Command.OperationId, Digest = RevisionCodec.Digest(intent.Command), intent.Handoff,
            NodeId, StorageGeneration, CredentialId, Fingerprint, Term, MembershipVersion, ConfirmedAtUtc
        });
    }
}

public sealed record VoterCredentialRetirementCall(VoterCredentialRetirementIntent Intent, VoterCredentialRetirementEvidence? Evidence);

public interface IVoterCredentialRetirementClient
{
    Task<CommandResult> RetireAsync(VoterCredentialRetirementIntent intent, CancellationToken token);
}
