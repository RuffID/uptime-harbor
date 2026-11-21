using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Cluster.Onboarding;
using UptimeHarbor.Application.Cluster.Onboarding.Renewal;
using UptimeHarbor.Application.Installation;
using UptimeHarbor.Infrastructure.Cluster;

namespace UptimeHarbor.Infrastructure.Installation.Renewal;

/// <summary>Публикует установленное продолжение через общий TLS snapshot под тем же Transition.</summary>
public class LocalVoterContinuationPublication(LocalVoterContinuationInstallation installed, InstallationState installation,
    InstallationLease lease, RevisionStateStore revisions, DynamicPeerTls tls, IOptions<ClusterOptions> options, TimeProvider time)
{
    public async Task<DynamicPeerTls.Snapshot> PublishAsync(string reference, CancellationToken token)
    {
        await installation.Transition.WaitAsync(token);
        try
        {
            LocalInstallationProfile profile = installation.Current
                ?? throw new InvalidOperationException("PeerCredentialProfileMissing");
            long recovery = revisions.RecoveryVersion;
            var evidence = await installed.VerifyAsync(reference, null, complete: false, token);
            var confirmed = LocalVoterCredentialInstallationCoordinator.RequireConfirmed(evidence);
            VoterCredentialRequest request = evidence.Local.Intent.Request;
            EnrollmentIssuance leaf = request.Renewal.Issuance;
            if (options.Value.TlsBoundary != "Direct" || profile.Mode != "Bootstrap"
                || profile.InstallIdentity != lease.InstallIdentity || profile.BootId != lease.BootId
                || profile.ClusterId != request.ClusterId || profile.ClusterGeneration != request.Generation
                || profile.NodeId != leaf.NodeId || profile.StorageGeneration != leaf.StorageGeneration || profile.PeerAddress != leaf.PeerUri)
            {
                throw new UnauthorizedAccessException("VoterContinuationPublicationProfileChanged");
            }

            RevisionImage image = evidence.Image;
            OnboardingContractImage contracts = image.OnboardingContracts!;
            AcceptedVoterContinuation accepted = contracts.VoterContinuations![request.OperationId];
            RevisionCommand registration = accepted.Registration!;
            OnlineProcessLease original = VoterContinuationReducer.Lease(registration, image.Results[registration.OperationId].Index);
            LocalVoterContinuationQuarantine quarantine = new(reference, accepted.Bind!, original);
            OnlineProcessLease? current = contracts.OnlineProcesses?.GetValueOrDefault(leaf.NodeId);
            DateTimeOffset now = time.GetUtcNow();
            if (current is null || current.BootId != lease.BootId || current.BootChallenge != lease.BootChallenge
                || current.CredentialContinuation && installation.CommonOpen || !quarantine.Matches(image, now))
            {
                throw new UnauthorizedAccessException("VoterContinuationPublicationProcessChanged");
            }

            var (intent, key) = installed.OpenIntent(reference);
            using (key)
            {
                if (intent != evidence.Local.Intent)
                {
                    throw new InvalidDataException("VoterContinuationPublicationIntentChanged");
                }

                AcceptedEnrollmentIssuer issuer = contracts.Issuers![leaf.IssuerEpoch];
                token.ThrowIfCancellationRequested();
                return LocalVoterCredentialPublication.PublishMaterial(tls, revisions, profile, image, recovery, confirmed.IssueId,
                    confirmed.CredentialRevision, evidence.Local.Artifact!.CertificateDer, key, issuer, continuation: quarantine);
            }
        }
        catch
        {
            tls.BlockPublication();
            throw;
        }
        finally
        {
            installation.Transition.Release();
        }
    }
}
