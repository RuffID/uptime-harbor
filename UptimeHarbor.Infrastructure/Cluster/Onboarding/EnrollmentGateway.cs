using System.Security.Cryptography;
using DotNext.Net.Cluster;
using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.Options;
using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Cluster.Onboarding;
using UptimeHarbor.Domain.Cluster;
using UptimeHarbor.Infrastructure.Cluster;
using UptimeHarbor.Infrastructure.Installation;

namespace UptimeHarbor.Infrastructure.Cluster.Onboarding;

/// <summary>Единственный преддоверенный путь к reserve/issue/bind/admit. Membership/snapshot здесь не выполняются.</summary>
public class EnrollmentGateway(EnrollmentDescriptors descriptors, EnrollmentNonces nonces,
    EnrollmentExchangeGate gate, EnrollmentCommandJournal journal, EnrollmentAuthority authority,
    EnrollmentSigner signer, RevisionDispatcher dispatcher, IOptions<ClusterOptions> options,
    IOptions<InstallationOptions> installation, TimeProvider time, IEnrollmentReadBarrier? readBarrier = null)
{
    public async Task<EnrollmentDescriptor> ConfirmProbeAsync(EnrollmentExchange exchange, CancellationToken token)
    {
        byte[] csr = Validate(exchange);
        try
        {
            if (exchange.Proof.Action != "ProbeEnrollmentPeers" || exchange.Invitation is not null || readBarrier is null)
            {
                throw new UnauthorizedAccessException("EnrollmentProbePurposeInvalid");
            }

            await readBarrier.ConfirmAsync(token);
            EnrollmentDescriptor descriptor = descriptors.Bound(exchange.Request, exchange.Proof);
            if (descriptor.AdmissionUntilUtc <= time.GetUtcNow() || descriptor.ProcessLeaseUntilUtc <= time.GetUtcNow())
            {
                throw new UnauthorizedAccessException("EnrollmentProbeAdmissionExpired");
            }

            return descriptor;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(csr);
        }
    }

    public async Task<EnrollmentState> StateAsync(EnrollmentExchange exchange, CancellationToken token)
    {
        byte[] csr = Validate(exchange);
        try
        {
            if (exchange.Proof.Action != "ReadEnrollmentAttempt" || exchange.Invitation is not null || readBarrier is null)
            {
                throw new UnauthorizedAccessException("EnrollmentReadPurposeInvalid");
            }

            await readBarrier.ConfirmAsync(token);
            RevisionImage image = descriptors.RequireImage();
            EnrollmentRequest request = exchange.Request;
            if (request.ClusterId != image.ClusterId || request.Generation != image.Generation)
            {
                throw new UnauthorizedAccessException("EnrollmentClusterMismatch");
            }

            OnboardingAttempt? attempt = image.Onboarding?.Invitations.GetValueOrDefault(request.InvitationId)?.Attempt;
            if (attempt is null)
            {
                return new(1, request, exchange.Proof, null);
            }

            return new(1, request, exchange.Proof, descriptors.Bound(request, exchange.Proof, readOnly: true));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(csr);
        }
    }

    public async Task<EnrollmentDescriptor> ExchangeAsync(EnrollmentExchange exchange, CancellationToken token)
    {
        byte[] csr = Validate(exchange);
        await gate.Semaphore.WaitAsync(token);
        try
        {
            EnrollmentRequest request = exchange.Request;
            EnrollmentProof proof = exchange.Proof;
            RevisionImage image = descriptors.RequireImage();
            if (request.ClusterId != image.ClusterId || request.Generation != image.Generation)
            {
                throw new UnauthorizedAccessException("EnrollmentClusterMismatch");
            }

            new SetupPeerAddressPolicy(installation.Value).ValidateUri(request.PeerUri);
            OnboardingAttempt? attempt = image.Onboarding?.Invitations.GetValueOrDefault(request.InvitationId)?.Attempt;
            if (attempt is not null)
            {
                descriptors.RequireCurrent(image, attempt, request);
            }
            else if (proof.Action != "ReserveInvitation")
            {
                throw new UnauthorizedAccessException("EnrollmentReservationRequired");
            }

            if (proof.Action == "ReserveInvitation" && exchange.Invitation is null
                || proof.Action != "ReserveInvitation" && exchange.Invitation is not null)
            {
                throw new UnauthorizedAccessException("EnrollmentSecretPurposeInvalid");
            }

            EnrollmentIssuedArtifact? artifact = null;
            if (proof.Action is "BindEnrollmentCredential" or "AdmitInvitedNode")
            {
                artifact = await signer.IssueAsync(request, attempt?.Issuance
                    ?? throw new UnauthorizedAccessException("EnrollmentIssuanceRequired"), csr, token);
            }

            RevisionCommand command = await journal.PrepareAsync(proof.RequestId,
                () => Prepare(exchange, descriptors.RequireImage(), artifact), token);
            RequireExact(exchange, command, attempt);
            CommandResult result = await authority.AcceptAsync(dispatcher, command, token);
            if (result.Outcome != OperationOutcome.Committed)
            {
                throw new InvalidOperationException("EnrollmentDecisionRejected/" + result.Reason);
            }

            EnrollmentDescriptor descriptor = descriptors.Bound(request, proof);
            if (proof.Action is "AuthorizeEnrollmentCertificate" or "BindEnrollmentCredential")
            {
                artifact = await signer.IssueAsync(request, descriptor.Issuance!, csr, token);
                descriptor = descriptor with { CertificateDer = Convert.ToBase64String(artifact.CertificateDer) };
            }

            return descriptor;
        }
        finally
        {
            gate.Semaphore.Release();
            CryptographicOperations.ZeroMemory(csr);
        }
    }

    private byte[] Validate(EnrollmentExchange exchange)
    {
        if (exchange.Version != 1 || exchange.CsrDer.Length is < 1 or > 87384)
        {
            throw new InvalidDataException("EnrollmentExchangeInvalid");
        }

        byte[] csr = Convert.FromBase64String(exchange.CsrDer);
        bool signature = EnrollmentProofs.Verify(exchange.Request, exchange.Proof, csr);
        if (!signature || !nonces.Consume(exchange.Request.AttemptId, exchange.Proof))
        {
            throw new UnauthorizedAccessException("EnrollmentProofRejected");
        }

        return csr;
    }

    private RevisionCommand Prepare(EnrollmentExchange exchange, RevisionImage image, EnrollmentIssuedArtifact? artifact)
    {
        EnrollmentRequest request = exchange.Request;
        EnrollmentProof proof = exchange.Proof;
        OnboardingAttempt? attempt = image.Onboarding?.Invitations.GetValueOrDefault(request.InvitationId)?.Attempt;
        OnboardingCommand data = new()
        {
            StorageGeneration = options.Value.StorageGeneration, InvitationId = request.InvitationId,
            AttemptId = request.AttemptId, BootId = proof.BootId, BootChallenge = proof.BootChallenge,
            ProcessEpoch = proof.ProcessEpoch
        };
        if (proof.Action == "ReserveInvitation")
        {
            Guid code = Guid.ParseExact(exchange.Invitation!, "D");
            data = data with
            {
                Request = request, RequestDigest = EnrollmentRequestCodec.Digest(request),
                Verifier = InvitationSecrets.Verifier(request.ClusterId, request.Generation, request.InvitationId, code)
            };
        }
        else if (proof.Action == "AuthorizeEnrollmentCertificate")
        {
            AcceptedEnrollmentIssuer issuer = image.OnboardingContracts!.Issuers!.Values.Single(item =>
                item.NodeId == options.Value.NodeId && item.StorageGeneration == options.Value.StorageGeneration
                && !item.RetiredAtUtc.HasValue && !item.RevokedAtUtc.HasValue);
            DateTimeOffset now = EnrollmentCertificates.Seconds(time.GetUtcNow());
            byte[] serial = RandomNumberGenerator.GetBytes(16);
            serial[0] = (byte)((serial[0] & 0x3f) + 1);
            EnrollmentIssuance template = new(1, proof.RequestId, issuer.Epoch, Convert.ToHexString(serial),
                request.NodeId, request.StorageGeneration, request.PeerUri, request.SpkiDigest, request.CsrDigest,
                now, now.AddDays(30));
            data = data with { IssueId = template.IssueId, Issuance = template };
        }
        else if (proof.Action == "BindEnrollmentCredential")
        {
            data = data with { IssueId = attempt!.IssueId, CertificateDigest = artifact!.CertificateDigest };
        }
        else if (proof.Action == "AdmitInvitedNode")
        {
            string member = ClusterMemberId.FromEndPoint(new UriEndPoint(new Uri(request.PeerUri))).ToString();
            OperationalNode node = new(request.NodeId, request.StorageGeneration, request.PeerUri, member, false, false, false, 0);
            OperationalCredential credential = new(attempt!.IssueId!.Value, request.NodeId, artifact!.CertificateDigest,
                Convert.ToBase64String(artifact.CertificateDer), attempt.Issuance!.NotAfterUtc, proof.RequestId, null);
            data = data with { Node = node, Credential = credential };
        }
        else if (proof.Action is not ("ResumeOnboardingAttempt" or "RenewOnboardingAdmission" or "RenewOnboardingProcess"))
        {
            throw new InvalidDataException("EnrollmentActionUnavailable");
        }

        return new()
        {
            Schema = 3, ClusterId = request.ClusterId, ClusterGeneration = request.Generation,
            OperationId = proof.RequestId, OriginNodeId = options.Value.NodeId, ActorUserId = Guid.Empty,
            ActorRevision = 0, EntityId = request.InvitationId, EntityKind = DeletedEntityKind.Node,
            Action = proof.Action, BaseRevision = image.Onboarding?.Revision ?? 0,
            CreatedAtUtc = time.GetUtcNow(), SystemSource = "OnboardingCoordinator", Onboarding = data
        };
    }

    private static void RequireExact(EnrollmentExchange exchange, RevisionCommand command, OnboardingAttempt? attempt)
    {
        EnrollmentRequest request = exchange.Request;
        EnrollmentProof proof = exchange.Proof;
        OnboardingCommand data = command.Onboarding!;
        bool process = data.BootId == proof.BootId && data.BootChallenge == proof.BootChallenge && data.ProcessEpoch == proof.ProcessEpoch;
        if (command.OperationId != proof.RequestId || command.Action != proof.Action || command.ClusterId != request.ClusterId
            || command.ClusterGeneration != request.Generation || data.InvitationId != request.InvitationId
            || data.AttemptId != request.AttemptId || !process)
        {
            throw new UnauthorizedAccessException("EnrollmentCommandBindingChanged");
        }

        if (proof.Action == "ReserveInvitation")
        {
            string verifier = InvitationSecrets.Verifier(request.ClusterId, request.Generation, request.InvitationId,
                Guid.ParseExact(exchange.Invitation!, "D"));
            if (data.Request != request || data.RequestDigest != proof.RequestDigest || !InvitationSecrets.Matches(data.Verifier!, verifier))
            {
                throw new UnauthorizedAccessException("EnrollmentReservationChanged");
            }
        }
        else if (attempt is null || attempt.Request != request)
        {
            throw new UnauthorizedAccessException("EnrollmentAttemptChanged");
        }
    }
}
