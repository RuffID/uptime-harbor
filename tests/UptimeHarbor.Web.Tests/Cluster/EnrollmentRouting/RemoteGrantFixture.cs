using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Cluster.Onboarding;
using UptimeHarbor.Application.Installation;
using UptimeHarbor.Domain.Cluster;
using UptimeHarbor.Infrastructure.Cluster.Onboarding;

namespace UptimeHarbor.Tests.Web.Cluster.EnrollmentRouting;

/// <summary>Синтетический уже принятый voter и local restart proof; проверяет RPC, а не полный Join/recovery.</summary>
public class RemoteGrantFixture : IAsyncDisposable
{
    public EnrollmentRoutingFixture Cluster { get; } = new();
    public EnrollmentRoutingNode Caller => Cluster.Ingress;
    public Guid InvitationId { get; } = Guid.NewGuid();
    public OnboardingAttempt Attempt { get; private set; } = null!;

    public async Task InitializeAsync(CancellationToken token)
    {
        await Cluster.InitializeAsync(token);
        RevisionImage image = Cluster.State.Current!;
        DateTimeOffset now = Cluster.First.Time.Now;
        Guid actor = Cluster.First.Account.ActorUserId;
        OnboardingDelegation delegation = new(actor, image.Values[actor].Revision, image.Index, now);
        LocalInstallationProfile profile = Caller.Store.Profile! with
        {
            ManagedRestart = true, ProfileRevision = Caller.Store.Profile!.ProfileRevision + 1
        };
        Caller.Store.Profile = profile;
        Caller.Installation.Publish(profile);
        using ECDsa key = Caller.Certificate.GetECDsaPrivateKey()!;
        byte[] csr = new CertificateRequest("CN=existing voter", key, HashAlgorithmName.SHA256).CreateSigningRequest();
        string spki = EnrollmentCertificates.Hash(key.ExportSubjectPublicKeyInfo());
        EnrollmentRequest request = new(1, image.ClusterId, image.Generation, InvitationId, profile.AttemptId!.Value,
            Caller.Options.NodeId, Caller.Options.StorageGeneration, Caller.Options.PublicUri, spki, EnrollmentCertificates.Hash(csr));
        AcceptedEnrollmentIssuer issuer = image.OnboardingContracts!.Issuers!.Values.Single();
        OperationalCredential credential = image.Operations.Credentials[Caller.Options.CredentialId];
        EnrollmentIssuance template = new(1, credential.Id, issuer.Epoch, Caller.Certificate.SerialNumber,
            request.NodeId, request.StorageGeneration, request.PeerUri, spki, request.CsrDigest,
            new DateTimeOffset(Caller.Certificate.NotBefore.ToUniversalTime()), credential.ExpiresAtUtc);
        Attempt = new(request, EnrollmentRequestCodec.Digest(request), Guid.NewGuid(),
            now.AddMinutes(30), delegation, Caller.Lease.BootId, 1, now.AddMinutes(2), now)
        {
            BootChallenge = Caller.Lease.BootChallenge, Status = OnboardingAttemptStatus.MembershipAccepted,
            IssueId = credential.Id, Issuance = template, IssuanceAuthorizationIndex = image.Index,
            CertificateDigest = credential.Fingerprint, AdmitOperationId = Guid.NewGuid(), AdmissionUntilUtc = now.AddMinutes(10)
        };
        OnboardingInvitation invitation = new(InvitationId, Guid.NewGuid(), new string('A', 64), now, now.AddMinutes(15), delegation)
        {
            Status = InvitationStatus.Consumed, Attempt = Attempt
        };
        OnboardingImage onboarding = image.Onboarding ?? new();
        OnlineProcessLease process = new(request.NodeId, Guid.NewGuid(), Caller.Lease.BootId, Caller.Lease.BootChallenge,
            1, Caller.Readiness.RecoveryVersion, now.AddMinutes(2), image.Index);
        Cluster.State.Publish(image with
        {
            Onboarding = onboarding with { Invitations = onboarding.Invitations.Add(InvitationId, invitation) },
            OnboardingContracts = image.OnboardingContracts with
            {
                OnlineProcesses = image.OnboardingContracts.OnlineProcesses!.SetItem(request.NodeId, process)
            }
        });
        RecoveryAdmission admission = new(image.ClusterId, image.Generation, request.NodeId, request.StorageGeneration,
            false, false, null, null, image.Index, image.Operations.MembershipVersion)
        {
            Proof = new ManagedRestartProof(Caller.Lease.InstallIdentity, Caller.Lease.BootId,
                Caller.Lease.BootChallenge, new string('B', 64)) { ActorId = actor }
        };
        Caller.Admission.Publish(profile, admission);
        Cluster.Refresh();
    }

    public OnlineGrant Grant(OnlineGrantPurpose purpose = OnlineGrantPurpose.Process)
    {
        RevisionImage image = Cluster.State.Current!;
        DateTimeOffset now = Cluster.First.Time.Now;
        EnrollmentRequest request = Attempt.Request;
        return new(1, purpose, image.ClusterId, image.Generation, request.AttemptId, InvitationId, Attempt.AdmitOperationId,
            request.NodeId, request.StorageGeneration, request.PeerUri, request.SpkiDigest, Attempt.IssueId!.Value,
            Attempt.CertificateDigest!, Attempt.Issuance!.IssuerEpoch, Guid.NewGuid(), Attempt.RequestDigest,
            Caller.Lease.BootId, Caller.Lease.BootChallenge, Attempt.ProcessEpoch, Attempt.Delegation.ActorId,
            Attempt.Delegation.ActorRevision, Attempt.Delegation.Revision, Caller.Readiness.RecoveryVersion,
            image.Operations.Revision, image.Operations.MembershipVersion, image.Index, null, now, now.AddMinutes(2), string.Empty);
    }

    public async ValueTask DisposeAsync()
    {
        await Cluster.DisposeAsync();
    }
}
