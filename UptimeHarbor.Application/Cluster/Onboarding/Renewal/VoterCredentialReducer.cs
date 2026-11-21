using System.Collections.Immutable;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Domain.Cluster;

namespace UptimeHarbor.Application.Cluster.Onboarding.Renewal;

/// <summary>Только accepted переходы; production admission закрыт до runtime PoP/CSR/majority проверки.</summary>
public static class VoterCredentialReducer
{
    public static RevisionImage Apply(RevisionImage image, RevisionCommand command, DateTimeOffset now)
    {
        EnrollmentTrustCommand data = command.EnrollmentTrust!;
        VoterCredentialCommand payload = data.Rotation!;
        VoterCredentialRenewal renewal = payload.Renewal;
        EnrollmentIssuance leaf = renewal.Issuance;
        OnboardingContractImage contracts = image.OnboardingContracts ?? new();
        ImmutableDictionary<Guid, VoterCredentialRotation> rotations = contracts.VoterCredentialRotations
            ?? ImmutableDictionary<Guid, VoterCredentialRotation>.Empty;
        OperationalNode? origin = image.Operations.Nodes.GetValueOrDefault(command.OriginNodeId);
        OperationalNode? node = image.Operations.Nodes.GetValueOrDefault(leaf.NodeId);
        OperationalCredential? old = image.Operations.Credentials.GetValueOrDefault(renewal.PreviousCredentialId);
        bool validOrigin = origin is not null && origin.Voting && !origin.Removed && origin.StorageGeneration == data.StorageGeneration;
        bool validNode = node is not null && node.Voting && !node.Removed && node.StorageGeneration == leaf.StorageGeneration
            && node.Address == leaf.PeerUri;
        bool validCredential = old is not null && old.NodeId == leaf.NodeId && old.Fingerprint == renewal.PreviousFingerprint;
        if (!validOrigin || !validNode || !validCredential || image.Operations.MaintenanceId != Guid.Empty)
        {
            return Decide(image, command, "VoterCredentialIdentityUnavailable");
        }

        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(old);
        VoterCredentialRotation? previous = rotations.GetValueOrDefault(leaf.IssueId);
        if (command.Action == "RetireOldCredential")
        {
            if (previous?.Renewal != renewal || previous.CredentialRevision <= 0 || previous.RetiredIndex.HasValue
                || now < previous.BoundAtUtc!.Value)
            {
                return Decide(image, command, "VoterCredentialRetirementUnavailable");
            }

            VoterCredentialRotation retired = previous with
            {
                RetireOperationId = command.OperationId, RetiredIndex = image.Index, RetiredAtUtc = now,
                RetireDigest = RevisionCodec.Digest(command), InstallationProofDigest = payload.InstallationProofDigest
            };
            image = Put(image, contracts, rotations, retired);
            image = image with { Operations = image.Operations with { Revision = image.Index } };
            return Decide(image, command, null);
        }

        VoterCredentialRotation? latest = VoterCredentialPolicy.Latest(image, node.NodeId);
        long revision = latest?.CredentialRevision ?? 0;
        bool currentKey = latest is null || latest.Renewal.Issuance.IssueId == old.Id
            && latest.Renewal.Issuance.SpkiDigest == renewal.PreviousSpkiDigest;
        if (revision != renewal.PreviousCredentialRevision || !currentKey)
        {
            return Decide(image, command, "VoterCredentialRevisionChanged");
        }

        if (!VoterCredentialPolicy.IsUsable(image, old, now))
        {
            return Decide(image, command, "VoterCredentialExpiredOrRevoked");
        }

        AcceptedEnrollmentIssuer? issuer = contracts.Issuers?.GetValueOrDefault(leaf.IssuerEpoch);
        OperationalNode? owner = null;
        if (issuer is not null)
        {
            owner = image.Operations.Nodes.GetValueOrDefault(issuer.NodeId);
        }

        bool activeIssuer = issuer is not null && !issuer.RevokedAtUtc.HasValue && !issuer.RetiredAtUtc.HasValue
            && now >= issuer.NotBeforeUtc && now < issuer.NotAfterUtc;
        bool validOwner = issuer is not null && owner is not null && owner.Voting && !owner.Removed
            && owner.StorageGeneration == issuer.StorageGeneration;
        bool lifetime = issuer is not null && leaf.NotBeforeUtc >= issuer.NotBeforeUtc
            && leaf.NotAfterUtc <= issuer.NotAfterUtc && now < leaf.NotAfterUtc;
        if (!activeIssuer || !validOwner || !lifetime)
        {
            return Decide(image, command, "VoterCredentialIssuerUnavailable");
        }

        if (command.Action == "AuthorizeVoterCredential")
        {
            DateTimeOffset intentAt = leaf.NotAfterUtc.AddDays(-30);
            bool busy = rotations.Values.Any(item => item.Renewal.Issuance.NodeId == node.NodeId && !item.RetiredIndex.HasValue);
            bool reused = image.Operations.Credentials.ContainsKey(leaf.IssueId)
                || rotations.ContainsKey(leaf.IssueId)
                || rotations.Values.Any(item => item.Renewal.Issuance.SpkiDigest == leaf.SpkiDigest
                    || item.Renewal.PreviousSpkiDigest == leaf.SpkiDigest)
                || image.Onboarding?.Invitations.Values.Any(item => item.Attempt?.IssueId == leaf.IssueId) == true;
            if (busy || reused || old.ExpiresAtUtc - now > TimeSpan.FromDays(7)
                || intentAt > now || now - intentAt > TimeSpan.FromSeconds(10))
            {
                return Decide(image, command, "VoterCredentialAuthorizationUnavailable");
            }

            VoterCredentialRotation authorized = new(renewal, command.OperationId, image.Index,
                RevisionCodec.Digest(command), now);
            image = Put(image, contracts, rotations, authorized);
            return Decide(image, command, null);
        }

        OperationalCredential credential = payload.Credential!;
        if (previous?.Renewal != renewal || previous.CredentialRevision != 0 || now < previous.AuthorizedAtUtc
            || image.Operations.Credentials.ContainsKey(credential.Id)
            || image.Operations.Credentials.Values.Any(item => item.Fingerprint == credential.Fingerprint))
        {
            return Decide(image, command, "VoterCredentialBindingUnavailable");
        }

        VoterCredentialRotation bound = previous with
        {
            BindOperationId = command.OperationId, CredentialRevision = image.Index, BindDigest = RevisionCodec.Digest(command),
            BoundAtUtc = now, OverlapUntilUtc = VoterCredentialPolicy.OverlapUntil(now, old.ExpiresAtUtc)
        };
        image = Put(image, contracts, rotations, bound);
        image = image with
        {
            Operations = image.Operations with
            {
                Credentials = image.Operations.Credentials.Add(credential.Id, credential), Revision = image.Index
            }
        };
        return Decide(image, command, null);
    }

    private static RevisionImage Put(RevisionImage image, OnboardingContractImage contracts,
        ImmutableDictionary<Guid, VoterCredentialRotation> rotations, VoterCredentialRotation updated)
    {
        return image with { OnboardingContracts = contracts with
        {
            VoterCredentialRotations = rotations.SetItem(updated.Renewal.Issuance.IssueId, updated)
        } };
    }

    private static RevisionImage Decide(RevisionImage image, RevisionCommand command, string? reason)
    {
        OperationOutcome outcome = OperationOutcome.Committed;
        if (reason is not null)
        {
            outcome = OperationOutcome.Rejected;
        }

        CommandResult decision = new(command.OperationId, RevisionCodec.Digest(command), outcome,
            image.Index, image.Term, image.Index, reason, null, null, 0);
        return image with { Results = image.Results.Add(command.OperationId, decision) };
    }
}
