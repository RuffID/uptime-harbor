using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Cluster.Onboarding;
using UptimeHarbor.Application.Cluster.Onboarding.Renewal;
using UptimeHarbor.Infrastructure.Cluster.Onboarding.Issuers;
using UptimeHarbor.Infrastructure.Installation;

namespace UptimeHarbor.Infrastructure.Cluster.Onboarding.Renewal;

/// <summary>Типизированная выдача voter leaf с общим issuer lock и журналом DER.</summary>
public class VoterCredentialSigner(IEnrollmentSigningUnitOfWork work, EnrollmentSigningJournal journal,
    EnrollmentIssuerSigningLock issuerLock, EnrollmentIssuerStorage storage, VoterCredentialRenewalAuthority authority,
    IOptions<ClusterOptions> options, TimeProvider time, ILogger<VoterCredentialSigner> logger)
{
    public Task<EnrollmentIssuedArtifact> IssueAsync(VoterCredentialSigningRequest call,
        Guid authenticatedNodeId, Guid authenticatedStorage, CancellationToken token)
    {
        return IssueCoreAsync(call.ProofRequest, async (_, cancellationToken) =>
        {
            RevisionImage image = await authority.ConfirmSigningAsync(call, authenticatedNodeId, authenticatedStorage, cancellationToken);
            return new(image, VoterCredentialProofs.Digest(call.ProofRequest.Request), call.AuthorizationIndex,
                _ => VoterCredentialAuthorityPolicy.RequireAccepted(image, call, time.GetUtcNow(), authority.LegacyTrust));
        }, token);
    }

    internal Task<EnrollmentIssuedArtifact> IssueContinuationAsync(VoterCredentialProofRequest call, string registrationDigest,
        VoterContinuationAuthority continuation, CancellationToken token)
    {
        return IssueCoreAsync(call, (consume, cancellationToken) =>
            continuation.ConfirmIssuanceAsync(call, registrationDigest, consume, cancellationToken), token);
    }

    private async Task<EnrollmentIssuedArtifact> IssueCoreAsync(VoterCredentialProofRequest call,
        Func<bool, CancellationToken, ValueTask<AcceptedIssue>> confirm, CancellationToken token)
    {
        VoterCredentialRequest request = call.Request;
        EnrollmentIssuance template = request.Renewal.Issuance;
        await issuerLock.Semaphore.WaitAsync(token);
        try
        {
            AcceptedIssue accepted = await confirm(true, token);
            EnrollmentIssuerReference local = await work.Signing.FindIssuerAsync(template.IssuerEpoch, token)
                ?? throw new UnauthorizedAccessException("VoterCredentialIssuerKeyUnavailable");
            VoterCredentialAuthorityPolicy.RequireLocalIssuer(accepted.Image, local, options.Value.NodeId,
                options.Value.StorageGeneration, template.IssuerEpoch, time.GetUtcNow());
            (InstallationLease lease, LocalSecretStore secrets) = storage.Open(create: false);
            if (local.InstallIdentity != lease.InstallIdentity)
            {
                throw new UnauthorizedAccessException("VoterCredentialIssuerInstallationMismatch");
            }

            EnrollmentIssuedArtifact? artifact = await work.Signing.FindIssueAsync(template.IssueId, token);
            bool created = artifact is null;
            if (artifact is null)
            {
                byte[] pem = secrets.Read(local.Epoch, "issuer", local.KeyReference);
                try
                {
                    using ECDsa key = ECDsa.Create();
                    key.ImportFromPem(Encoding.UTF8.GetString(pem));
                    using X509Certificate2 root = X509CertificateLoader.LoadCertificate(local.CertificateDer);
                    if (EnrollmentCertificates.Hash(key.ExportSubjectPublicKeyInfo()) != local.SpkiDigest
                        || EnrollmentCertificates.Hash(root.PublicKey.ExportSubjectPublicKeyInfo()) != local.SpkiDigest
                        || EnrollmentCertificates.Hash(root.RawData) != local.CertificateDigest)
                    {
                        throw new UnauthorizedAccessException("VoterCredentialIssuerKeyMismatch");
                    }

                    using X509Certificate2 issuer = root.CopyWithPrivateKey(key);
                    using X509Certificate2 leaf = EnrollmentCertificates.IssueLeaf(issuer,
                        VoterCredentialProofs.CertificateIdentity(request), template, VoterCredentialProofs.ReadCsr(call));
                    artifact = new(template.IssueId, template.IssuerEpoch, request.OperationId,
                        accepted.Digest, EnrollmentSigningJournal.TemplateDigest(template), null,
                        accepted.Index, leaf.RawData, EnrollmentCertificates.Hash(leaf.RawData));
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(pem);
                }
            }

            VoterCredentialArtifactValidation.RequireIssued(accepted.Image, request, artifact, accepted.Digest, accepted.Index, time.GetUtcNow());
            accepted.RequireCurrent(token);
            EnrollmentIssuedArtifact saved = await journal.SaveVoterIssueOnceAsync(artifact, request, accepted.Digest, time.GetUtcNow(), token);
            // Commit мог завершиться с потерей ответа; повтор всегда читает исходные DER. Новый barrier перед ответом обязателен.
            accepted = await confirm(false, token);
            VoterCredentialAuthorityPolicy.RequireLocalIssuer(accepted.Image, local, options.Value.NodeId,
                options.Value.StorageGeneration, template.IssuerEpoch, time.GetUtcNow());
            VoterCredentialArtifactValidation.RequireIssued(accepted.Image, request, saved, accepted.Digest, accepted.Index, time.GetUtcNow());
            accepted.RequireCurrent(token);
            if (created)
            {
                logger.LogInformation("Voter certificate persisted {OperationId} {IssueId} {NodeId} at {AuthorizationIndex}.",
                    request.OperationId, saved.IssueId, template.NodeId, saved.AuthorizationIndex);
            }

            return saved;
        }
        catch (Exception error) when (error is UnauthorizedAccessException or InvalidDataException or CryptographicException or FormatException
            || error is InvalidOperationException { Message: "EnrollmentReadMajorityUnavailable" or "EnrollmentReadMajorityChanged" })
        {
            logger.LogWarning("Voter certificate signing rejected {OperationId} {IssueId}.",
                request.OperationId, template.IssueId);
            throw;
        }
        finally
        {
            issuerLock.Semaphore.Release();
        }
    }

    internal sealed record AcceptedIssue(RevisionImage Image, string Digest, long Index, Action<CancellationToken> RequireCurrent);
}
