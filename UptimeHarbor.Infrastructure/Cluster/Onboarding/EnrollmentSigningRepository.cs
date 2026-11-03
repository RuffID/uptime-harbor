using Microsoft.EntityFrameworkCore;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Cluster.Onboarding;
using UptimeHarbor.Infrastructure.Persistence;

namespace UptimeHarbor.Infrastructure.Cluster.Onboarding;

/// <summary>SingleOrDefault сохраняет проверку повреждения/дублирования; SQL остаётся внутри адаптера.</summary>
public class EnrollmentSigningRepository(UptimeHarborDbContext context) : IEnrollmentSigningRepository
{
    public async Task<IReadOnlyList<EnrollmentIssuerReference>> ListIssuersAsync(CancellationToken token)
    {
        List<LocalEnrollmentIssuerRecord> rows = await context.Set<LocalEnrollmentIssuerRecord>().AsNoTracking().ToListAsync(token);
        return rows.Select(row => new EnrollmentIssuerReference(row.Id, row.InstallIdentity, row.ClusterId,
            row.ClusterGeneration, row.NodeId, row.StorageGeneration, row.KeyReference, row.CertificateDer,
            row.CertificateDigest, row.SpkiDigest)).ToArray();
    }

    public async Task<EnrollmentIssuerReference?> FindIssuerAsync(Guid epoch, CancellationToken token)
    {
        LocalEnrollmentIssuerRecord? row = await context.Set<LocalEnrollmentIssuerRecord>().AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == epoch, token);
        if (row is null)
        {
            return null;
        }

        return new(row.Id, row.InstallIdentity, row.ClusterId, row.ClusterGeneration, row.NodeId,
            row.StorageGeneration, row.KeyReference, row.CertificateDer, row.CertificateDigest, row.SpkiDigest);
    }

    public async Task<EnrollmentIssuedArtifact?> FindIssueAsync(Guid issueId, CancellationToken token)
    {
        LocalEnrollmentIssuanceRecord? row = await context.Set<LocalEnrollmentIssuanceRecord>().AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == issueId, token);
        if (row is null)
        {
            return null;
        }

        return new(row.Id, row.IssuerEpoch, row.AttemptId, row.RequestDigest, row.TemplateDigest, row.GenesisId,
            row.AuthorizationIndex, row.CertificateDer, row.CertificateDigest);
    }

    public void AddIssuer(EnrollmentIssuerReference issuer)
    {
        context.Add(new LocalEnrollmentIssuerRecord
        {
            Id = issuer.Epoch, InstallIdentity = issuer.InstallIdentity, ClusterId = issuer.ClusterId,
            ClusterGeneration = issuer.Generation, NodeId = issuer.NodeId, StorageGeneration = issuer.StorageGeneration,
            KeyReference = issuer.KeyReference, CertificateDer = issuer.CertificateDer.ToArray(),
            CertificateDigest = issuer.CertificateDigest, SpkiDigest = issuer.SpkiDigest
        });
    }

    public void AddIssue(EnrollmentIssuedArtifact artifact)
    {
        context.Add(new LocalEnrollmentIssuanceRecord
        {
            Id = artifact.IssueId, IssuerEpoch = artifact.IssuerEpoch, AttemptId = artifact.AttemptId,
            RequestDigest = artifact.RequestDigest, TemplateDigest = artifact.TemplateDigest, GenesisId = artifact.GenesisId,
            AuthorizationIndex = artifact.AuthorizationIndex, CertificateDer = artifact.CertificateDer.ToArray(),
            CertificateDigest = artifact.CertificateDigest
        });
    }
}
