using System.Collections.Immutable;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Domain.Cluster;

namespace UptimeHarbor.Application.Cluster.Onboarding;

/// <summary>Issuer registry в том же WAL. Подпись CA не добавляет credential в operations.</summary>
public static class EnrollmentTrustReducer
{
    public static RevisionImage Apply(RevisionImage image, RevisionCommand command)
    {
        EnrollmentTrustCommand data = command.EnrollmentTrust!;
        if (!data.CoordinatorAtUtc.HasValue || data.CoordinatorTerm != image.Term)
        {
            throw new InvalidDataException("EnrollmentTrustAcceptanceUnconfirmed");
        }

        DateTimeOffset now = data.CoordinatorAtUtc.Value;
        OnboardingContractImage contracts = image.OnboardingContracts ?? new();
        ImmutableDictionary<Guid, AcceptedEnrollmentIssuer> issuers = contracts.Issuers ?? ImmutableDictionary<Guid, AcceptedEnrollmentIssuer>.Empty;
        if (command.Action == "InitializeCluster")
        {
            return Initialize(image, command, issuers, now);
        }

        OperationalNode? origin = image.Operations.Nodes.GetValueOrDefault(command.OriginNodeId);
        if (origin is null || !origin.Voting || origin.Removed || origin.StorageGeneration != data.StorageGeneration
            || image.Operations.MaintenanceId != Guid.Empty
            || !OnboardingReducer.Active(image, command.ActorUserId, command.ActorRevision))
        {
            return Decide(image, command, "EnrollmentTrustActorUnavailable");
        }

        TimeSpan age = now - data.ReauthenticatedAtUtc!.Value;
        if (age < TimeSpan.Zero || age > TimeSpan.FromMinutes(5))
        {
            return Decide(image, command, "ReauthenticationExpired");
        }

        AcceptedEnrollmentIssuer? previous = issuers.GetValueOrDefault(data.IssuerEpoch);
        if (command.BaseRevision != (previous?.Revision ?? 0))
        {
            return Decide(image, command, "EnrollmentIssuerRevisionChanged");
        }

        AcceptedEnrollmentIssuer updated;
        if (command.Action == "RegisterEnrollmentIssuer")
        {
            AcceptedEnrollmentIssuer issuer = data.Issuer!;
            OperationalNode? owner = image.Operations.Nodes.GetValueOrDefault(issuer.NodeId);
            bool duplicate = issuers.Values.Any(item => item.CertificateDigest == issuer.CertificateDigest || item.SpkiDigest == issuer.SpkiDigest);
            if (previous is not null || duplicate || owner is null || owner.Removed || !owner.Voting
                || owner.StorageGeneration != issuer.StorageGeneration || now < issuer.NotBeforeUtc || now >= issuer.NotAfterUtc)
            {
                return Decide(image, command, "EnrollmentIssuerRegistrationUnavailable");
            }

            updated = issuer with { Revision = image.Index };
        }
        else
        {
            if (previous is null)
            {
                return Decide(image, command, "EnrollmentIssuerMissing");
            }

            updated = previous with { Revision = image.Index };
            if (command.Action == "RetireEnrollmentIssuer")
            {
                updated = updated with { RetiredAtUtc = previous.RetiredAtUtc ?? now };
            }
            else
            {
                updated = updated with { RevokedAtUtc = previous.RevokedAtUtc ?? now };
            }
        }

        image = image with { OnboardingContracts = contracts with { Issuers = issuers.SetItem(updated.Epoch, updated) } };
        return Decide(image, command, null);
    }

    private static RevisionImage Initialize(RevisionImage image, RevisionCommand command,
        ImmutableDictionary<Guid, AcceptedEnrollmentIssuer> issuers, DateTimeOffset now)
    {
        EnrollmentTrustCommand data = command.EnrollmentTrust!;
        GenesisAuthorization genesis = data.Genesis!;
        AcceptedEnrollmentIssuer issuer = data.Issuer!;
        OperationalNode node = data.Node!;
        OperationalCredential credential = data.Credential!;
        bool authorized = image.OnboardingContracts?.Genesis == genesis && image.OnboardingContracts.GenesisInitialized == false
            && image.BootstrapAccountDigest == genesis.AccountOrImportDigest && !genesis.MigrationId.HasValue
            && genesis.ClusterId == image.ClusterId && genesis.Generation == image.Generation
            && command.OriginNodeId == genesis.NodeId && data.StorageGeneration == genesis.StorageGeneration;
        bool empty = image.Operations.Nodes.IsEmpty && image.Operations.Credentials.IsEmpty && issuers.IsEmpty
            && image.Values.IsEmpty && image.Monitoring.Nodes.IsEmpty && image.RecoveryBaseline is null
            && image.Operations.MaintenanceId == Guid.Empty;
        bool exact = node.NodeId == genesis.NodeId && node.StorageGeneration == genesis.StorageGeneration
            && node.Address == genesis.PeerUri && node.Voting && !node.Activated && !node.Removed && node.Revision == 0
            && issuer.NodeId == node.NodeId && issuer.StorageGeneration == node.StorageGeneration
            && issuer.CertificateDigest == genesis.IssuerCertificateDigest && issuer.SpkiDigest == genesis.IssuerSpkiDigest
            && now >= issuer.NotBeforeUtc && now < issuer.NotAfterUtc
            && credential.NodeId == node.NodeId && credential.Id != Guid.Empty && credential.ExpiresAtUtc > now
            && credential.AdmissionId is null && credential.AdmissionExpiresAtUtc is null
            && credential.RevokedAtUtc is null && credential.ConsumedAtUtc is null;
        if (!authorized || !empty || !exact)
        {
            return Decide(image, command, "GenesisNotAuthorized");
        }

        StorageGuards.Digest(credential.Fingerprint);
        StorageGuards.PublicCertificate(credential.PublicCertificate);
        image = image with
        {
            OnboardingContracts = image.OnboardingContracts! with
            {
                GenesisInitialized = true, Issuers = issuers.Add(issuer.Epoch, issuer with { Revision = image.Index })
            },
            Operations = image.Operations with
            {
                Nodes = image.Operations.Nodes.Add(node.NodeId, node with { Revision = image.Index }),
                Credentials = image.Operations.Credentials.Add(credential.Id, credential),
                Revision = image.Index, BotId = data.BotId, MembershipVersion = data.GenesisMembershipVersion
            },
            Monitoring = image.Monitoring with { Nodes = image.Monitoring.Nodes.Add(node.NodeId, node.StorageGeneration) }
        };
        return Decide(image, command, null);
    }

    private static RevisionImage Decide(RevisionImage image, RevisionCommand command, string? reason)
    {
        OperationOutcome outcome = OperationOutcome.Committed;
        if (reason is not null)
        {
            outcome = OperationOutcome.Rejected;
        }

        CommandResult decision = new(command.OperationId, RevisionCodec.Digest(command), outcome,
            image.Index, image.Term, image.Index, reason, null, null, command.ActorRevision);
        return image with { Results = image.Results.Add(command.OperationId, decision) };
    }
}
