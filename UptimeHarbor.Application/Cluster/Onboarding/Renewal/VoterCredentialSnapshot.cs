using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Domain.Cluster;

namespace UptimeHarbor.Application.Cluster.Onboarding.Renewal;

public static class VoterCredentialSnapshot
{
    public static void Validate(RevisionImage image)
    {
        Dictionary<Guid, VoterCredentialRotation> latest = [];
        Dictionary<Guid, VoterCredentialRotation> latestBound = [];
        HashSet<string> newKeys = new(StringComparer.Ordinal);
        foreach (KeyValuePair<Guid, VoterCredentialRotation> item in (image.OnboardingContracts?.VoterCredentialRotations ?? [])
            .OrderBy(item => item.Value.AuthorizedIndex))
        {
            VoterCredentialRotation rotation = item.Value;
            VoterCredentialRenewal renewal = rotation.Renewal;
            EnrollmentIssuance leaf = renewal.Issuance;
            VoterCredentialCommands.ValidateRenewal(renewal);
            OperationalNode? node = image.Operations.Nodes.GetValueOrDefault(leaf.NodeId);
            OperationalCredential? old = image.Operations.Credentials.GetValueOrDefault(renewal.PreviousCredentialId);
            AcceptedEnrollmentIssuer? issuer = image.OnboardingContracts!.Issuers?.GetValueOrDefault(leaf.IssuerEpoch);
            VoterCredentialRotation? prior = latest.GetValueOrDefault(leaf.NodeId);
            bool continuation = rotation.ContinuationRequestId.HasValue;
            if (continuation)
            {
                prior = latestBound.GetValueOrDefault(leaf.NodeId);
            }

            RequireDecision(image, rotation.AuthorizationOperationId, rotation.AuthorizedIndex, rotation.AuthorizationDigest);
            DateTimeOffset intentAt = leaf.NotAfterUtc.AddDays(-30);
            StorageGuards.Utc(rotation.AuthorizedAtUtc);
            bool identity = item.Key == leaf.IssueId && node is not null && node.StorageGeneration == leaf.StorageGeneration
                && node.Address == leaf.PeerUri && old is not null && old.NodeId == leaf.NodeId && old.Fingerprint == renewal.PreviousFingerprint;
            bool timely = old is not null && rotation.AuthorizedAtUtc < old.ExpiresAtUtc
                && old.ExpiresAtUtc - rotation.AuthorizedAtUtc <= TimeSpan.FromDays(7)
                && intentAt <= rotation.AuthorizedAtUtc && rotation.AuthorizedAtUtc - intentAt <= TimeSpan.FromSeconds(10);
            if (rotation.ContinuationRequestId is Guid requestId)
            {
                RevisionCommand? bind = image.OnboardingContracts.VoterContinuations?.GetValueOrDefault(requestId)?.Bind;
                timely = old is not null && rotation.AuthorizedAtUtc >= old.ExpiresAtUtc
                    && bind is not null && bind.OperationId == rotation.BindOperationId && rotation.CredentialRevision > 0
                    && bind.VoterContinuation?.Context.Request.Renewal == renewal;
            }

            bool lifetime = issuer is not null && leaf.NotBeforeUtc >= issuer.NotBeforeUtc && leaf.NotAfterUtc <= issuer.NotAfterUtc;
            bool chain = renewal.PreviousCredentialRevision == (prior?.CredentialRevision ?? 0);
            if (prior is not null)
            {
                bool priorClosed = prior.RetiredIndex.HasValue && prior.RetiredIndex < rotation.AuthorizedIndex;
                if (continuation)
                {
                    priorClosed = prior.CredentialRevision < rotation.AuthorizedIndex;
                }

                chain = chain && priorClosed
                    && prior.Renewal.Issuance.IssueId == old?.Id && prior.Renewal.Issuance.SpkiDigest == renewal.PreviousSpkiDigest;
            }

            if (!identity || !timely || !lifetime || !chain || !newKeys.Add(leaf.SpkiDigest))
            {
                throw new InvalidDataException("VoterCredentialSnapshotInvalid");
            }

            bool recordedByPending = continuation && latest.GetValueOrDefault(leaf.NodeId) is { CredentialRevision: 0 } pending
                && pending.Renewal.PreviousSpkiDigest == renewal.PreviousSpkiDigest;
            if (prior is null && !recordedByPending && !newKeys.Add(renewal.PreviousSpkiDigest))
            {
                throw new InvalidDataException("VoterCredentialSnapshotKeyReused");
            }

            ArgumentNullException.ThrowIfNull(old);
            ValidateBinding(image, rotation, old);
            latest[leaf.NodeId] = rotation;
            if (rotation.CredentialRevision > 0)
            {
                latestBound[leaf.NodeId] = rotation;
            }
        }
    }

    private static void ValidateBinding(RevisionImage image, VoterCredentialRotation rotation, OperationalCredential old)
    {
        bool bound = rotation.CredentialRevision > 0;
        bool retired = rotation.RetiredIndex.HasValue;
        if (rotation.CredentialRevision < 0 || bound != rotation.BindOperationId.HasValue || bound != (rotation.BindDigest is not null)
            || bound != rotation.BoundAtUtc.HasValue || bound != rotation.OverlapUntilUtc.HasValue
            || retired != rotation.RetireOperationId.HasValue || retired != (rotation.RetireDigest is not null)
            || retired != rotation.RetiredAtUtc.HasValue || retired != (rotation.InstallationProofDigest is not null) || retired && !bound)
        {
            throw new InvalidDataException("VoterCredentialSnapshotBoundFieldsInvalid");
        }

        OperationalCredential? credential = image.Operations.Credentials.GetValueOrDefault(rotation.Renewal.Issuance.IssueId);
        if (!bound)
        {
            if (credential is not null)
            {
                throw new InvalidDataException("VoterCredentialSnapshotOrphanCredential");
            }

            return;
        }

        RequireDecision(image, rotation.BindOperationId!.Value, rotation.CredentialRevision, rotation.BindDigest!);
        StorageGuards.Utc(rotation.BoundAtUtc!.Value);
        StorageGuards.Utc(rotation.OverlapUntilUtc!.Value);
        if (rotation.CredentialRevision <= rotation.AuthorizedIndex || rotation.BoundAtUtc < rotation.AuthorizedAtUtc
            || !rotation.ContinuationRequestId.HasValue && rotation.BoundAtUtc >= old.ExpiresAtUtc
            || rotation.BoundAtUtc >= rotation.Renewal.Issuance.NotAfterUtc
            || rotation.OverlapUntilUtc != VoterCredentialPolicy.OverlapUntil(rotation.BoundAtUtc.Value, old.ExpiresAtUtc)
            || credential is null || credential.NodeId != old.NodeId || credential.ExpiresAtUtc != rotation.Renewal.Issuance.NotAfterUtc
            || credential.AdmissionId.HasValue || credential.AdmissionExpiresAtUtc.HasValue || credential.ConsumedAtUtc.HasValue)
        {
            throw new InvalidDataException("VoterCredentialSnapshotBindingInvalid");
        }

        VoterCredentialCommands.ValidateCredential(credential);
        if (retired)
        {
            RequireDecision(image, rotation.RetireOperationId!.Value, rotation.RetiredIndex!.Value, rotation.RetireDigest!);
            StorageGuards.Utc(rotation.RetiredAtUtc!.Value);
            if (rotation.RetiredIndex <= rotation.CredentialRevision || rotation.RetiredAtUtc < rotation.BoundAtUtc)
            {
                throw new InvalidDataException("VoterCredentialSnapshotRetirementInvalid");
            }

            if (StorageGuards.Digest(rotation.InstallationProofDigest!) != rotation.InstallationProofDigest)
            {
                throw new InvalidDataException("VoterCredentialSnapshotProofInvalid");
            }
        }
    }

    private static void RequireDecision(RevisionImage image, Guid operationId, long index, string digest)
    {
        CommandResult? decision = image.Results.GetValueOrDefault(operationId);
        if (index <= 0 || index > image.Index || decision?.Outcome != OperationOutcome.Committed
            || decision.Index != index || decision.Digest != digest)
        {
            throw new InvalidDataException("VoterCredentialSnapshotDecisionMissing");
        }
    }
}
