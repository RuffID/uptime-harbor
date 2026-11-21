using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Cluster.Onboarding;
using UptimeHarbor.Application.Cluster.Onboarding.Renewal;
using UptimeHarbor.Infrastructure.Cluster.Onboarding;
using UptimeHarbor.Infrastructure.Cluster.Onboarding.Renewal;
using UptimeHarbor.Infrastructure.Installation.Renewal.Models;

namespace UptimeHarbor.Infrastructure.Installation.Renewal;

/// <summary>Локальная подготовка до Bind. Pins требуют прежнюю B1 authority; dispatch/signer/TLS не вызываются.</summary>
public class LocalVoterCredentialPreparation(LocalVoterCredentialFiles files, LocalSecretStore secrets,
    InstallationLease lease, InstallationState installation, VoterCredentialRenewalAuthority authority,
    TimeProvider time, ILogger<LocalVoterCredentialPreparation> logger)
{
    public async Task<LocalVoterCredentialState> PrepareAsync(RevisionImage image, Guid nodeId, Guid previousCredentialId,
        Guid issuerEpoch, Guid previousKeyOwner, string previousKeyReference, DateTimeOffset now, CancellationToken token)
    {
        await installation.Transition.WaitAsync(token);
        try
        {
            if (now.Offset != TimeSpan.Zero)
            {
                throw new InvalidDataException("VoterCredentialLocalUtcRequired");
            }

            OperationalNode node = image.Operations.Nodes[nodeId];
            string reference = LocalVoterCredentialMaterial.Reference(lease.InstallIdentity, image.ClusterId, image.Generation,
                nodeId, node.StorageGeneration, previousCredentialId);
            LocalVoterCredentialState? existing = ReadCore(reference);
            if (existing is not null)
            {
                if (existing.Intent.PreviousKeyOwner != previousKeyOwner || existing.Intent.PreviousKeyReference != previousKeyReference
                    || existing.Intent.Request.Renewal.Issuance.IssuerEpoch != issuerEpoch
                    || existing.Intent.Request.Renewal.Issuance.PeerUri != node.Address)
                {
                    throw new InvalidDataException("VoterCredentialLocalPreparationChanged");
                }

                return existing;
            }

            OperationalCredential old = image.Operations.Credentials[previousCredentialId];
            using X509Certificate2 previous = X509CertificateLoader.LoadCertificate(Convert.FromBase64String(old.PublicCertificate));
            if (EnrollmentCertificates.Hash(previous.RawData) != old.Fingerprint
                || new DateTimeOffset(previous.NotAfter.ToUniversalTime()) != old.ExpiresAtUtc
                || !VoterCredentialLeafTrust.ValidatePrevious(image, old, previous, now, authority.LegacyTrust))
            {
                throw new InvalidDataException("VoterCredentialLocalPreviousLeafInvalid");
            }

            using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            byte[] csr = new CertificateRequest("CN=UptimeHarbor voter renewal", key, HashAlgorithmName.SHA256).CreateSigningRequest();
            byte[] serial = RandomNumberGenerator.GetBytes(16);
            serial[0] = (byte)((serial[0] & 0x3F) + 1);
            DateTimeOffset start = new(now.Ticks - now.Ticks % TimeSpan.TicksPerSecond, TimeSpan.Zero);
            EnrollmentIssuance issuance = new(1, Guid.NewGuid(), issuerEpoch, Convert.ToHexString(serial), nodeId, node.StorageGeneration, node.Address,
                EnrollmentCertificates.Hash(key.ExportSubjectPublicKeyInfo()), EnrollmentCertificates.Hash(csr),
                start.AddMinutes(-2), start.AddDays(30));
            long revision = VoterCredentialPolicy.Latest(image, nodeId)?.CredentialRevision ?? 0;
            VoterCredentialRenewal renewal = new(previousCredentialId, old.Fingerprint,
                EnrollmentCertificates.Hash(previous.PublicKey.ExportSubjectPublicKeyInfo()), revision, issuance);
            VoterCredentialRequest request = new(1, image.ClusterId, image.Generation, Guid.NewGuid(), renewal);
            VoterCredentialProofs.Digest(request);
            VoterCredentialAuthorityPolicy.RequireCurrent(image, request, now);
            LocalVoterCredentialIntent intent = new(1, lease.InstallIdentity, reference, request, Convert.ToBase64String(csr),
                previousKeyOwner, previousKeyReference);
            using ECDsa previousKey = LocalVoterCredentialMaterial.OpenPrevious(intent, secrets, files);
            byte[] bundle = LocalVoterCredentialMaterial.Encode(intent, key);
            try
            {
                files.WriteOnce(reference, "intent", bundle);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(bundle);
            }

            LocalVoterCredentialState saved = ReadCore(reference) ?? throw new InvalidDataException("VoterCredentialLocalIntentMissing");
            logger.LogInformation("VoterCredentialLocalIntentSaved {OperationId} {IssueId}", request.OperationId, issuance.IssueId);
            return saved;
        }
        finally
        {
            installation.Transition.Release();
        }
    }

    public async Task<LocalVoterCredentialState> PinAuthorizationAsync(string reference,
        VoterCredentialSigningRequest call, CancellationToken token)
    {
        await installation.Transition.WaitAsync(token);
        try
        {
            LocalVoterCredentialState saved = RequireCall(reference, call);
            EnrollmentIssuance template = saved.Intent.Request.Renewal.Issuance;
            RevisionImage image = await authority.ConfirmSigningAsync(call, template.NodeId, template.StorageGeneration, token);
            DateTimeOffset now = time.GetUtcNow();
            VoterCredentialRotation rotation = VoterCredentialAuthorityPolicy.RequireAccepted(image, call, now, authority.LegacyTrust);
            AcceptedEnrollmentIssuer issuer = image.OnboardingContracts!.Issuers![call.ProofRequest.Request.Renewal.Issuance.IssuerEpoch];
            LocalVoterCredentialAuthorization authorization = new(VoterCredentialProofs.Digest(saved.Intent.Request), rotation, issuer);
            LocalVoterCredentialMaterial.RequireAuthorization(saved.Intent, authorization);
            files.WriteOnce(reference, "authorization", StrictOnboardingJson.Encode(authorization));
            LocalVoterCredentialState pinned = ReadCore(reference)!;
            if (saved.Authorization is null)
            {
                logger.LogInformation("VoterCredentialLocalAuthorizationPinned {OperationId} {AuthorizationIndex}",
                    saved.Intent.Request.OperationId, rotation.AuthorizedIndex);
            }

            return pinned;
        }
        finally
        {
            installation.Transition.Release();
        }
    }

    public async Task<LocalVoterCredentialState> PinArtifactAsync(string reference,
        VoterCredentialSigningRequest call, EnrollmentIssuedArtifact artifact, CancellationToken token)
    {
        await installation.Transition.WaitAsync(token);
        try
        {
            LocalVoterCredentialState saved = RequireCall(reference, call);
            LocalVoterCredentialAuthorization authorization = saved.Authorization
                ?? throw new InvalidDataException("VoterCredentialLocalAuthorizationMissing");
            // Свежая прежняя B1 authority обязательна и при повторе после потерянного ответа.
            EnrollmentIssuance template = saved.Intent.Request.Renewal.Issuance;
            RevisionImage image = await authority.ConfirmSigningAsync(call, template.NodeId, template.StorageGeneration, token);
            DateTimeOffset now = time.GetUtcNow();
            EnrollmentIssuedArtifact copy = artifact with { CertificateDer = artifact.CertificateDer.ToArray() };
            VoterCredentialArtifactValidation.Require(image, call, copy, now, authority.LegacyTrust);
            LocalVoterCredentialAuthorization current = new(VoterCredentialProofs.Digest(saved.Intent.Request),
                image.OnboardingContracts!.VoterCredentialRotations![copy.IssueId],
                image.OnboardingContracts.Issuers![copy.IssuerEpoch]);
            if (current != authorization)
            {
                throw new InvalidDataException("VoterCredentialLocalAuthorizationChanged");
            }

            LocalVoterCredentialMaterial.RequireArtifact(saved.Intent, authorization, copy);
            files.WriteOnce(reference, "artifact", StrictOnboardingJson.Encode(copy));
            LocalVoterCredentialState pinned = ReadCore(reference)!;
            if (saved.Artifact is null)
            {
                logger.LogInformation("VoterCredentialLocalArtifactPinned {OperationId} {IssueId}",
                    saved.Intent.Request.OperationId, copy.IssueId);
            }

            return pinned;
        }
        finally
        {
            installation.Transition.Release();
        }
    }

    public async Task<LocalVoterCredentialState> ReadAsync(string reference, CancellationToken token)
    {
        await installation.Transition.WaitAsync(token);
        try
        {
            return ReadCore(reference) ?? throw new InvalidDataException("VoterCredentialLocalIntentMissing");
        }
        finally
        {
            installation.Transition.Release();
        }
    }

    private LocalVoterCredentialState RequireCall(string reference, VoterCredentialSigningRequest call)
    {
        LocalVoterCredentialState saved = ReadCore(reference) ?? throw new InvalidDataException("VoterCredentialLocalIntentMissing");
        if (call.ProofRequest.Request != saved.Intent.Request || call.ProofRequest.CsrDer != saved.Intent.CsrDer)
        {
            throw new InvalidDataException("VoterCredentialLocalRequestChanged");
        }

        return saved;
    }

    // Вызывается соседним Bind coordinator только под тем же InstallationState.Transition.
    internal LocalVoterCredentialState? ReadCore(string reference)
    {
        var material = LocalVoterCredentialRetry.ReadIntent(files, lease.InstallIdentity, reference);
        if (material is not { } saved)
        {
            if (files.HasSlot(reference))
            {
                throw new InvalidDataException("VoterCredentialLocalIntentMissing");
            }

            return null;
        }

        (LocalVoterCredentialIntent intent, ECDsa key) = saved;
        using ECDsa currentKey = key;
        using ECDsa previousKey = LocalVoterCredentialMaterial.OpenPrevious(intent, secrets, files);

        byte[]? authorizationBytes = files.Read(reference, "authorization");
        byte[]? artifactBytes = files.Read(reference, "artifact");
        LocalVoterCredentialAuthorization? authorization = null;
        if (authorizationBytes is not null)
        {
            authorization = StrictOnboardingJson.Decode<LocalVoterCredentialAuthorization>(authorizationBytes);
            LocalVoterCredentialMaterial.RequireAuthorization(intent, authorization);
        }

        EnrollmentIssuedArtifact? artifact = null;
        if (artifactBytes is not null)
        {
            if (authorization is null)
            {
                throw new InvalidDataException("VoterCredentialLocalAuthorizationMissing");
            }

            artifact = StrictOnboardingJson.Decode<EnrollmentIssuedArtifact>(artifactBytes);
            LocalVoterCredentialMaterial.RequireArtifact(intent, authorization, artifact);
        }

        return new(intent, authorization, artifact);
    }

    internal async Task<LocalVoterCredentialState> ReplaceRejectedAsync(string reference,
        VoterCredentialProofRequest call, CancellationToken token)
    {
        await installation.Transition.WaitAsync(token);
        try
        {
            LocalVoterCredentialState saved = ReadCore(reference)
                ?? throw new InvalidDataException("VoterCredentialLocalIntentMissing");
            if (saved.Intent.Request != call.Request || saved.Intent.CsrDer != call.CsrDer || saved.Authorization is not null)
            {
                throw new InvalidDataException("VoterCredentialLocalRetryChanged");
            }

            RevisionImage image = await authority.ReadBindingAsync(call.Request, token);
            DateTimeOffset now = time.GetUtcNow();
            VoterCredentialProofs.Require(image, call, now, authority.LegacyTrust);
            LocalVoterCredentialRetry.Save(files, saved.Intent, image, now);
            return ReadCore(reference) ?? throw new InvalidDataException("VoterCredentialLocalIntentMissing");
        }
        finally
        {
            installation.Transition.Release();
        }
    }

    // Координатор Bind удерживает Transition и повторно прочитал закреплённый intent.
    internal VoterCredentialBindCall ProveBind(LocalVoterCredentialState local, VoterCredentialSigningRequest signing,
        RevisionCommand command)
    {
        using ECDsa key = LocalVoterCredentialMaterial.OpenPrevious(local.Intent, secrets, files);
        VoterCredentialProof proof = signing.ProofRequest.Proof with { IssuedAtUtc = time.GetUtcNow() };
        return new(command, signing, local.Artifact!, VoterCredentialProofs.SignBind(signing, command, proof, key));
    }

    internal async Task<VoterCredentialProofRequest> ProveAsync(LocalVoterCredentialState local,
        VoterCredentialProof proof, CancellationToken token)
    {
        await installation.Transition.WaitAsync(token);
        try
        {
            LocalVoterCredentialIntent intent = local.Intent;
            if (ReadCore(intent.KeyReference)?.Intent != intent)
            {
                throw new InvalidDataException("VoterCredentialLocalIntentChanged");
            }

            using ECDsa key = LocalVoterCredentialMaterial.OpenPrevious(intent, secrets, files);
            return new(intent.Request, VoterCredentialProofs.Sign(intent.Request, proof, key), intent.CsrDer);
        }
        finally
        {
            installation.Transition.Release();
        }
    }
}
