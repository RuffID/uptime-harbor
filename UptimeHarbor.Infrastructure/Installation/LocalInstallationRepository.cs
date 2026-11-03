using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using UptimeHarbor.Application.Installation;
using UptimeHarbor.Infrastructure.Persistence;

namespace UptimeHarbor.Infrastructure.Installation;

public class LocalInstallationRepository(UptimeHarborDbContext context) : ILocalInstallationRepository
{
    public async Task<LocalInstallationProfile?> ReadAsync(CancellationToken token)
    {
        LocalProfileRecord? row = await context.Set<LocalProfileRecord>().SingleOrDefaultAsync(token);
        if (row is null)
        {
            return null;
        }

        LocalInstallationProfile profile = JsonSerializer.Deserialize<LocalInstallationProfile>(row.Payload)
            ?? throw new InvalidDataException("InstallationProfileInvalid");
        if (profile.InstallIdentity != row.InstallIdentity || profile.ProfileRevision != row.Revision
            || !Enum.IsDefined(profile.Phase) || profile.RuntimeGeneration < 1 || profile.SetupEpoch < 1
            || profile.ProfileRevision < 1 || profile.BootId == Guid.Empty
            || profile.Mode is not ("Setup" or "Bootstrap" or "Join" or "Standalone"))
        {
            throw new InvalidDataException("InstallationProfileInvalid");
        }

        if (profile.AttemptId.HasValue && (profile.NodeId is null || profile.StorageGeneration is null
            || profile.KeyReference is null || profile.RequestDigest is null || profile.OperationIds is null))
        {
            throw new InvalidDataException("InstallationIdentityIncomplete");
        }

        return profile;
    }

    public void Create(LocalInstallationProfile profile)
    {
        context.Add(new LocalProfileRecord
        {
            InstallIdentity = profile.InstallIdentity, Revision = profile.ProfileRevision,
            Payload = JsonSerializer.Serialize(profile)
        });
    }

    public async Task ReplaceAsync(LocalInstallationProfile profile, long expectedRevision, CancellationToken token)
    {
        LocalProfileRecord row = await context.Set<LocalProfileRecord>().SingleAsync(token);
        if (row.Revision != expectedRevision || profile.ProfileRevision != expectedRevision + 1
            || row.InstallIdentity != profile.InstallIdentity)
        {
            throw new InvalidOperationException("InstallationRevisionConflict");
        }

        context.Entry(row).Property(item => item.Revision).OriginalValue = expectedRevision;
        row.Revision = profile.ProfileRevision;
        row.Payload = JsonSerializer.Serialize(profile);
    }

    public async Task<LocalAttemptStep?> FindStepAsync(Guid operationId, CancellationToken token)
    {
        LocalStepRecord? row = await context.Set<LocalStepRecord>().AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == operationId, token);
        if (row is null)
        {
            return null;
        }

        return JsonSerializer.Deserialize<LocalAttemptStep>(row.Payload)
            ?? throw new InvalidDataException("InstallationStepInvalid");
    }

    public void AddStep(LocalAttemptStep step)
    {
        context.Add(new LocalStepRecord
        {
            Id = step.OperationId, AttemptId = step.AttemptId, Payload = JsonSerializer.Serialize(step)
        });
    }
}
