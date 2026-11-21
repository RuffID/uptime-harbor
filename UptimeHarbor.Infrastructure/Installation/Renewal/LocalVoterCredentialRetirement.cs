using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Cluster.Onboarding;
using UptimeHarbor.Application.Cluster.Onboarding.Renewal;
using UptimeHarbor.Application.Installation;
using UptimeHarbor.Infrastructure.Cluster.Onboarding;
using UptimeHarbor.Infrastructure.Cluster.Onboarding.Renewal;

namespace UptimeHarbor.Infrastructure.Installation.Renewal;

internal sealed class LocalVoterCredentialRetirement(LocalVoterCredentialFiles files,
    LocalVoterCredentialInstallationCoordinator installed, DynamicPeerTls tls, VoterCredentialRenewalAuthority authority,
    IRevisionTransport transport, TimeProvider time, IVoterCredentialHandoffPeer? peer = null,
    IVoterCredentialRetirementClient? retirementClient = null)
{
    internal async Task ReconcileAsync(string reference, VoterCredentialRotation rotation, DynamicPeerTls.Snapshot snapshot,
        RevisionImage image, CancellationToken token)
    {
        DateTimeOffset now = time.GetUtcNow();
        if (rotation.OverlapUntilUtc is not { } deadline || now < deadline)
        {
            return;
        }

        var pin = files.Read(reference, LocalVoterCredentialFiles.RETIRE_STAGE);
        if (rotation.RetiredIndex.HasValue && pin is null)
        {
            // Accepted history can predate the worker's local intent.
            return;
        }

        var slot = await installed.ReadInstalledAsync(reference, token);
        if (snapshot.Quarantined || snapshot.CredentialId != slot.IssueId
            || snapshot.CredentialRevision != slot.CredentialRevision || snapshot.Fingerprint != slot.CertificateFingerprint
            || slot.IssueId != rotation.Renewal.Issuance.IssueId || slot.CredentialRevision != rotation.CredentialRevision)
        {
            throw new UnauthorizedAccessException("VoterCredentialHandoffPublicationChanged");
        }

        RevisionImage fresh = await authority.ReadFreshAsync(token);
        RequireCurrent(image, fresh, snapshot, token);
        VoterCredentialHandoff handoff = new(slot.IssueId, slot.CredentialRevision, rotation.BindDigest!, slot.CertificateFingerprint);
        RevisionCommand? pinned = pin is null ? null : StrictOnboardingJson.Decode<RevisionCommand>(pin);
        VoterCredentialRetirementIntent intent = VoterCredentialRetirementIntent.Create(fresh, rotation, handoff,
            pinned?.OperationId ?? Guid.NewGuid());
        RevisionCommand command = intent.Command;
        if (pinned is not null && pinned != command)
        {
            throw new InvalidDataException("VoterCredentialRetirementIntentChanged");
        }

        if (VoterCredentialRetirementIntent.CheckDecision(fresh, rotation, command) is not null)
        {
            return;
        }

        EnrollmentIssuance leaf = rotation.Renewal.Issuance;
        handoff.RequireSubject(fresh, leaf.NodeId, leaf.StorageGeneration, time.GetUtcNow());
        if (retirementClient is null)
        {
            await ConfirmHandoffAsync(fresh, snapshot, handoff, leaf.NodeId, token);
        }

        RevisionImage beforeSend = await authority.ReadFreshAsync(token);
        RequireCurrent(fresh, beforeSend, snapshot, token);
        handoff.RequireSubject(beforeSend, leaf.NodeId, leaf.StorageGeneration, time.GetUtcNow());

        files.WriteOnce(reference, LocalVoterCredentialFiles.RETIRE_STAGE, StrictOnboardingJson.Encode(command));
        RequireCurrent(beforeSend, beforeSend, snapshot, token);
        if (time.GetUtcNow() < deadline)
        {
            throw new UnauthorizedAccessException("VoterCredentialRetirementOverlapActive");
        }

        IReadOnlyList<CommandResult> response = retirementClient is null
            ? await transport.SendAsync([command], token)
            : [await retirementClient.RetireAsync(intent, token)];
        RevisionImage applied = await authority.ReadFreshAsync(token);
        RuntimeGenerationContext.Verify();
        var accepted = applied.OnboardingContracts?.VoterCredentialRotations?.GetValueOrDefault(handoff.IssueId);
        if (accepted is null || accepted.Renewal != rotation.Renewal || accepted.BindDigest != handoff.BindDigest
            || accepted.CredentialRevision != handoff.CredentialRevision || accepted.OverlapUntilUtc != deadline
            || VoterCredentialRetirementIntent.CheckDecision(applied, accepted, command) is not { } result
            || response.Count != 1 || response[0] != result)
        {
            throw new InvalidDataException("VoterCredentialRetirementResponseChanged");
        }
    }

    private async Task ConfirmHandoffAsync(RevisionImage image, DynamicPeerTls.Snapshot snapshot,
        VoterCredentialHandoff handoff, Guid subject, CancellationToken token)
    {
        OperationalNode[] voters = image.Operations.Nodes.Values.Where(node => node.Voting && !node.Removed).ToArray();
        if (voters.Length == 1 && voters[0].NodeId == subject)
        {
            byte[] nonce = RandomNumberGenerator.GetBytes(32);
            using ECDsa? key = snapshot.Certificate.GetECDsaPrivateKey();
            using ECDsa? verifier = snapshot.Certificate.GetECDsaPublicKey();
            if (key is null || verifier is null
                || !verifier.VerifyData(nonce, key.SignData(nonce, HashAlgorithmName.SHA256), HashAlgorithmName.SHA256))
            {
                throw new UnauthorizedAccessException("VoterCredentialHandoffKeyUnavailable");
            }

            return;
        }

        if (peer is not null)
        {
            foreach (OperationalNode witness in voters.Where(node => node.NodeId != subject && node.Activated).OrderBy(node => node.NodeId))
            {
                try
                {
                    await peer.ConfirmAsync(witness, handoff, snapshot, image, token);
                    return;
                }
                catch (Exception error) when (!token.IsCancellationRequested && error is HttpRequestException or TimeoutException)
                {
                    // A different voter can attest over its own mutually authenticated connection.
                }
            }
        }

        throw new UnauthorizedAccessException("VoterCredentialHandoffPeerUnavailable");
    }

    private void RequireCurrent(RevisionImage expected, RevisionImage current, DynamicPeerTls.Snapshot snapshot,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        RuntimeGenerationContext.Verify();
        if (!ReferenceEquals(expected, current) || !ReferenceEquals(snapshot, tls.Capture())
            || !authority.IsCurrent(current))
        {
            throw new UnauthorizedAccessException("VoterCredentialHandoffEvidenceChanged");
        }
    }

}
