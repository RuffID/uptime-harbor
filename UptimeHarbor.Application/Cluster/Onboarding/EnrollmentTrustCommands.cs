using System.Security.Cryptography;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Domain.Cluster;

namespace UptimeHarbor.Application.Cluster.Onboarding;

public static class EnrollmentTrustCommands
{
    public static bool IsTrust(RevisionCommand command)
    {
        return command.Action is "InitializeCluster" or "RegisterEnrollmentIssuer" or "RetireEnrollmentIssuer" or "RevokeEnrollmentIssuer";
    }

    public static void Validate(RevisionCommand command)
    {
        EnrollmentTrustCommand data = command.EnrollmentTrust ?? throw new InvalidDataException("EnrollmentTrustMissing");
        bool genesis = command.Action == "InitializeCluster";
        bool registration = genesis || command.Action == "RegisterEnrollmentIssuer";
        bool mixed = command.Onboarding is not null || command.Operation is not null || command.Value is not null
            || command.Monitoring is not null || command.Delivery is not null || command.Target is not null
            || command.Replacement is not null || command.AccountAction is not null || command.ResolvesConflictId.HasValue;
        if (!IsTrust(command) || command.Schema != 3 || data.Version != 1 || mixed
            || command.EntityKind != DeletedEntityKind.Node || command.EntityId != data.IssuerEpoch
            || registration != (data.Issuer is not null) || genesis != (data.Genesis is not null)
            || genesis != (data.Node is not null) || genesis != (data.Credential is not null)
            || !genesis && data.BotId != 0 || data.BotId < 0 || genesis == data.ReauthenticatedAtUtc.HasValue
            || data.GenesisMembershipVersion < 0 || !genesis && data.GenesisMembershipVersion != 0
            || !data.CoordinatorAtUtc.HasValue && data.GenesisMembershipVersion != 0)
        {
            throw new InvalidDataException("EnrollmentTrustEnvelopeInvalid");
        }

        StorageGuards.Id(data.StorageGeneration, nameof(data.StorageGeneration));
        StorageGuards.Id(data.IssuerEpoch, nameof(data.IssuerEpoch));
        if (genesis)
        {
            if (command.SystemSource != "GenesisController" || command.ActorUserId != Guid.Empty || command.ActorRevision != 0
                || command.BaseRevision != 0)
            {
                throw new InvalidDataException("GenesisControllerRequired");
            }

            OnboardingDataCodec.Validate(data.Genesis!);
        }
        else
        {
            StorageGuards.Id(command.ActorUserId, nameof(command.ActorUserId));
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(command.ActorRevision);
            StorageGuards.Utc(data.ReauthenticatedAtUtc!.Value);
            if (command.SystemSource is not null)
            {
                throw new InvalidDataException("EnrollmentTrustActorInvalid");
            }
        }

        if (data.CoordinatorAtUtc.HasValue)
        {
            StorageGuards.Utc(data.CoordinatorAtUtc.Value);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(data.CoordinatorTerm);
        }
        else if (data.CoordinatorTerm != 0)
        {
            throw new InvalidDataException("EnrollmentTrustTimestampMissing");
        }

        if (data.Issuer is not null)
        {
            ValidateIssuer(data.Issuer);
            if (data.Issuer.Epoch != data.IssuerEpoch || data.Issuer.Revision != 0
                || data.Issuer.RevokedAtUtc.HasValue || data.Issuer.RetiredAtUtc.HasValue)
            {
                throw new InvalidDataException("EnrollmentIssuerRegistrationInvalid");
            }
        }
    }

    public static void ValidateIssuer(AcceptedEnrollmentIssuer issuer)
    {
        StorageGuards.Id(issuer.Epoch, nameof(issuer.Epoch));
        StorageGuards.Id(issuer.NodeId, nameof(issuer.NodeId));
        StorageGuards.Id(issuer.StorageGeneration, nameof(issuer.StorageGeneration));
        StorageGuards.Utc(issuer.NotBeforeUtc);
        StorageGuards.Utc(issuer.NotAfterUtc);
        StorageGuards.Digest(issuer.SpkiDigest);
        StorageGuards.Digest(issuer.CertificateDigest);
        if (issuer.Purpose != EnrollmentIssuer.PEER_PURPOSE || issuer.Revision < 0
            || issuer.NotAfterUtc <= issuer.NotBeforeUtc || issuer.NotAfterUtc > issuer.NotBeforeUtc.AddYears(3)
            || issuer.CertificateDer.Length is < 1 or > 87384)
        {
            throw new InvalidDataException("EnrollmentIssuerInvalid");
        }

        byte[] der;
        try
        {
            der = Convert.FromBase64String(issuer.CertificateDer);
        }
        catch (FormatException)
        {
            throw new InvalidDataException("EnrollmentIssuerEncodingInvalid");
        }

        if (der.Length is < 1 or > 65536 || Convert.ToBase64String(der) != issuer.CertificateDer
            || Convert.ToHexString(SHA256.HashData(der)) != issuer.CertificateDigest)
        {
            throw new InvalidDataException("EnrollmentIssuerDigestInvalid");
        }

        foreach (DateTimeOffset? transition in new[] { issuer.RetiredAtUtc, issuer.RevokedAtUtc })
        {
            if (transition.HasValue)
            {
                StorageGuards.Utc(transition.Value);
                if (transition < issuer.NotBeforeUtc)
                {
                    throw new InvalidDataException("EnrollmentIssuerTransitionInvalid");
                }
            }
        }
    }
}
