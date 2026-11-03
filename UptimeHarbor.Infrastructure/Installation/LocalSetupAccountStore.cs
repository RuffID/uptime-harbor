using Microsoft.EntityFrameworkCore;
using UptimeHarbor.Infrastructure.Persistence;
using UptimeHarbor.Application.Installation;
using UptimeHarbor.Application.Cluster.Models;
using System.Text.Json;
using UptimeHarbor.Application.Cluster;

namespace UptimeHarbor.Infrastructure.Installation;

public class LocalSetupAccountStore(UptimeHarborDbContext context) : ILocalSetupAccountStore
{
    public async Task<LocalSetupAccount?> ReadAsync(CancellationToken token)
    {
        return await context.Set<LocalSetupAccount>().SingleOrDefaultAsync(token);
    }

    public void Create(LocalSetupAccount account)
    {
        context.Add(account);
    }

    public async Task<bool> HasProgressAsync(CancellationToken token)
    {
        return await context.Set<LocalAttemptRecord>().AnyAsync(token) || await context.Set<LocalStepRecord>().AnyAsync(token);
    }

    public async Task ValidateProgressAsync(LocalInstallationProfile profile, CancellationToken token)
    {
        List<LocalAttemptRecord> attempts = await context.Set<LocalAttemptRecord>().AsNoTracking().Take(2).ToListAsync(token);
        if (attempts.Count > 1 || attempts.Any(item => item.Id != profile.AttemptId))
        {
            throw new InvalidDataException("ForeignLocalAttempt");
        }

        foreach (LocalAttemptRecord row in attempts)
        {
            LocalOnboardingAttempt attempt = JsonSerializer.Deserialize<LocalOnboardingAttempt>(row.Payload)
                ?? throw new InvalidDataException("LocalAttemptInvalid");
            bool matching = attempt.AttemptId == profile.AttemptId && attempt.Request.NodeId == profile.NodeId
                && attempt.Request.StorageGeneration == profile.StorageGeneration && attempt.PrivateKeyReference == profile.KeyReference;
            if (!matching)
            {
                throw new InvalidDataException("LocalAttemptIdentityMismatch");
            }
        }

        List<LocalStepRecord> steps = await context.Set<LocalStepRecord>().AsNoTracking().Take(10001).ToListAsync(token);
        if (steps.Count > 10000 || steps.Any(item => item.AttemptId != profile.AttemptId))
        {
            throw new InvalidDataException("ForeignLocalAttemptStep");
        }

        foreach (LocalStepRecord row in steps)
        {
            LocalAttemptStep step = JsonSerializer.Deserialize<LocalAttemptStep>(row.Payload)
                ?? throw new InvalidDataException("LocalStepInvalid");
            if (step.OperationId != row.Id || step.AttemptId != row.AttemptId)
            {
                throw new InvalidDataException("LocalStepIdentityMismatch");
            }

            if (profile.OperationIds?.Values.Contains(row.Id) == true)
            {
                continue;
            }

            RevisionCommand command = JsonSerializer.Deserialize<RevisionCommand>(step.Evidence
                ?? throw new InvalidDataException("LocalStepEvidenceMissing"))
                ?? throw new InvalidDataException("LocalStepEvidenceInvalid");
            string prefix = string.Empty;
            if (step.Kind.StartsWith("Enrollment/", StringComparison.Ordinal))
            {
                prefix = "Enrollment/";
            }

            LocalCommandStepValidation.Require(profile, step, command, prefix);
        }
    }
}
