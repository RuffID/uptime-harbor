using System.Security.Cryptography;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Domain.Cluster;

namespace UptimeHarbor.Application.Cluster.Onboarding.Renewal;

public static class VoterCredentialCommands
{
    public static bool IsRotation(RevisionCommand command)
    {
        return command.Action is "AuthorizeVoterCredential" or "BindVoterCredential" or "RetireOldCredential";
    }

    public static void Validate(RevisionCommand command)
    {
        EnrollmentTrustCommand data = command.EnrollmentTrust!;
        VoterCredentialCommand rotation = data.Rotation ?? throw new InvalidDataException("VoterCredentialPayloadMissing");
        VoterCredentialRenewal renewal = rotation.Renewal;
        ValidateRenewal(renewal);
        bool bind = command.Action == "BindVoterCredential";
        bool retire = command.Action == "RetireOldCredential";
        bool mixed = command.Onboarding is not null || command.OnlineLifecycle is not null || command.Operation is not null
            || command.Value is not null || command.Monitoring is not null || command.Delivery is not null
            || command.Target is not null || command.Replacement is not null || command.AccountAction is not null
            || command.ResolvesConflictId.HasValue;
        if (command.Schema != 3 || data.Version != 1 || !IsRotation(command) || mixed
            || command.SystemSource != "VoterCredentialLifecycle" || command.ActorUserId != Guid.Empty || command.ActorRevision != 0
            || command.EntityKind != DeletedEntityKind.Node || command.EntityId != renewal.Issuance.NodeId
            || command.BaseRevision != renewal.PreviousCredentialRevision || data.IssuerEpoch != renewal.Issuance.IssuerEpoch
            || data.StorageGeneration == Guid.Empty || data.Issuer is not null || data.Genesis is not null
            || data.Node is not null || data.Credential is not null || data.BotId != 0
            || data.ReauthenticatedAtUtc.HasValue || data.GenesisMembershipVersion != 0
            || bind != (rotation.Credential is not null) || retire != (rotation.InstallationProofDigest is not null))
        {
            throw new InvalidDataException("VoterCredentialEnvelopeInvalid");
        }

        if (rotation.Credential is not null)
        {
            OperationalCredential credential = rotation.Credential;
            ValidateCredential(credential);
            if (credential.Id != renewal.Issuance.IssueId || credential.NodeId != command.EntityId
                || credential.ExpiresAtUtc != renewal.Issuance.NotAfterUtc || credential.AdmissionId.HasValue
                || credential.AdmissionExpiresAtUtc.HasValue || credential.ConsumedAtUtc.HasValue || credential.RevokedAtUtc.HasValue)
            {
                throw new InvalidDataException("VoterCredentialBindingInvalid");
            }
        }

        if (rotation.InstallationProofDigest is not null)
        {
            RequireDigest(rotation.InstallationProofDigest);
        }

        if (data.CoordinatorAtUtc.HasValue)
        {
            StorageGuards.Utc(data.CoordinatorAtUtc.Value);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(data.CoordinatorTerm);
        }
        else if (data.CoordinatorTerm != 0)
        {
            throw new InvalidDataException("VoterCredentialTimestampMissing");
        }
    }

    public static void ValidateRenewal(VoterCredentialRenewal renewal)
    {
        EnrollmentIssuance leaf = renewal.Issuance;
        foreach (Guid id in new[] { renewal.PreviousCredentialId, leaf.IssueId, leaf.IssuerEpoch, leaf.NodeId, leaf.StorageGeneration })
        {
            StorageGuards.Id(id, nameof(renewal));
        }

        foreach (string digest in new[] { renewal.PreviousFingerprint, renewal.PreviousSpkiDigest, leaf.SpkiDigest, leaf.CsrDigest })
        {
            RequireDigest(digest);
        }

        StorageGuards.Utc(leaf.NotBeforeUtc);
        StorageGuards.Utc(leaf.NotAfterUtc);
        ClusterOperationValidation.ValidateAddress(leaf.PeerUri);
        bool serial = leaf.Serial.Length == 32 && leaf.Serial.All(char.IsAsciiHexDigit)
            && leaf.Serial == leaf.Serial.ToUpperInvariant();
        if (serial)
        {
            serial = Convert.FromHexString(leaf.Serial)[0] is > 0 and < 128;
        }

        TimeSpan duration = leaf.NotAfterUtc - leaf.NotBeforeUtc;
        bool lifetime = leaf.NotBeforeUtc.Ticks % TimeSpan.TicksPerSecond == 0
            && leaf.NotAfterUtc.Ticks % TimeSpan.TicksPerSecond == 0
            && duration >= TimeSpan.FromDays(30) && duration <= TimeSpan.FromDays(30).Add(TimeSpan.FromMinutes(2));
        if (leaf.Version != 1 || renewal.PreviousCredentialRevision < 0 || renewal.PreviousCredentialId == leaf.IssueId
            || renewal.PreviousSpkiDigest == leaf.SpkiDigest || !serial
            || !lifetime)
        {
            throw new InvalidDataException("VoterCredentialRenewalInvalid");
        }
    }

    public static void ValidateCredential(OperationalCredential credential)
    {
        StorageGuards.Id(credential.Id, nameof(credential.Id));
        StorageGuards.Id(credential.NodeId, nameof(credential.NodeId));
        StorageGuards.Utc(credential.ExpiresAtUtc);
        RequireDigest(credential.Fingerprint);
        StorageGuards.PublicCertificate(credential.PublicCertificate);
        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(credential.PublicCertificate);
        }
        catch (FormatException)
        {
            throw new InvalidDataException("VoterCredentialDerInvalid");
        }

        if (bytes.Length == 0 || Convert.ToBase64String(bytes) != credential.PublicCertificate
            || Convert.ToHexString(SHA256.HashData(bytes)) != credential.Fingerprint)
        {
            throw new InvalidDataException("VoterCredentialDerMismatch");
        }
    }

    private static void RequireDigest(string digest)
    {
        if (StorageGuards.Digest(digest) != digest)
        {
            throw new InvalidDataException("VoterCredentialDigestNotCanonical");
        }
    }
}
