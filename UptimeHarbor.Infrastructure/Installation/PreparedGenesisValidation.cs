using System.Security.Cryptography.X509Certificates;
using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Installation;
using UptimeHarbor.Infrastructure.Cluster.Onboarding;

namespace UptimeHarbor.Infrastructure.Installation;

public static class PreparedGenesisValidation
{
    public static void Validate(LocalInstallationProfile profile, SetupAuthorityEvidence evidence, DateTimeOffset now)
    {
        PreparedGenesis prepared = evidence.Genesis ?? throw new InvalidDataException("GenesisIntentMissing");
        GenesisAuthorization genesis = prepared.Authorization;
        EnrollmentTrustCommand data = prepared.InitializeCluster.EnrollmentTrust
            ?? throw new InvalidDataException("GenesisInitializeMissing");
        bool identity = evidence.Kind == SetupAuthorityEvidenceKind.GenesisPrepared && evidence.AcceptedIndex == 0
            && evidence.AttemptId == profile.AttemptId && evidence.Digest == profile.BootstrapAccountDigest
            && genesis.GenesisId == profile.AttemptId && genesis.InstallIdentity == profile.InstallIdentity
            && genesis.ClusterId == profile.ClusterId && genesis.Generation == profile.ClusterGeneration
            && genesis.NodeId == profile.NodeId && genesis.StorageGeneration == profile.StorageGeneration
            && genesis.PeerUri == profile.PeerAddress && genesis.AccountOrImportDigest == profile.BootstrapAccountDigest
            && genesis.MigrationId is null && data.Genesis == genesis && data.BotId == profile.BotId
            && prepared.InitializeCluster.OperationId == genesis.GenesisId
            && prepared.Request.AttemptId == profile.AttemptId && prepared.Request.InvitationId == profile.AttemptId
            && prepared.Template.IssueId == profile.OperationIds!["admit"];
        if (!identity)
        {
            throw new InvalidDataException("GenesisIntentBindingInvalid");
        }

        RevisionCodec.Validate(prepared.InitializeCluster);
        EnrollmentTrustValidation.Validate(prepared.InitializeCluster, now);
        EnrollmentCertificates.ReadCsr(prepared.CsrDer, prepared.Request);
        using X509Certificate2 root = X509CertificateLoader.LoadCertificate(Convert.FromBase64String(data.Issuer!.CertificateDer));
        using X509Certificate2 leaf = X509CertificateLoader.LoadCertificate(Convert.FromBase64String(data.Credential!.PublicCertificate));
        if (prepared.Request.ClusterId != genesis.ClusterId || prepared.Request.Generation != genesis.Generation
            || prepared.Request.NodeId != genesis.NodeId || prepared.Request.StorageGeneration != genesis.StorageGeneration
            || prepared.Request.PeerUri != genesis.PeerUri || prepared.Template.IssuerEpoch != data.IssuerEpoch
            || !EnrollmentCertificates.ValidateLeaf(leaf, root, prepared.Request, prepared.Template,
                data.Credential.Fingerprint, now, true))
        {
            throw new InvalidDataException("GenesisIntentCertificateInvalid");
        }
    }
}
