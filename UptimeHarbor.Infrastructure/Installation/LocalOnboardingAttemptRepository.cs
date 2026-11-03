using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using UptimeHarbor.Application.Abstractions.Persistence;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Infrastructure.Persistence;

namespace UptimeHarbor.Infrastructure.Installation;

public class LocalOnboardingAttemptRepository(UptimeHarborDbContext context) : ILocalOnboardingAttemptRepository
{
    public async Task<LocalOnboardingAttempt?> FindAsync(Guid attemptId, CancellationToken token)
    {
        LocalAttemptRecord? row = await context.Set<LocalAttemptRecord>().SingleOrDefaultAsync(item => item.Id == attemptId, token);
        if (row is null)
        {
            return null;
        }

        LocalOnboardingAttempt attempt = JsonSerializer.Deserialize<LocalOnboardingAttempt>(row.Payload)
            ?? throw new InvalidDataException("LocalAttemptInvalid");
        if (attempt.AttemptId != row.Id || attempt.Revision != row.Revision)
        {
            throw new InvalidDataException("LocalAttemptInvalid");
        }

        return attempt;
    }

    public void Create(LocalOnboardingAttempt attempt)
    {
        context.Add(new LocalAttemptRecord
        {
            Id = attempt.AttemptId, Revision = attempt.Revision, Payload = JsonSerializer.Serialize(attempt)
        });
    }

    public async Task ReplaceAsync(LocalOnboardingAttempt attempt, long expectedRevision, CancellationToken token)
    {
        LocalAttemptRecord row = await context.Set<LocalAttemptRecord>().SingleAsync(item => item.Id == attempt.AttemptId, token);
        LocalOnboardingAttempt original = JsonSerializer.Deserialize<LocalOnboardingAttempt>(row.Payload)
            ?? throw new InvalidDataException("LocalAttemptInvalid");
        bool identityChanged = original.Request != attempt.Request || original.RequestDigest != attempt.RequestDigest
            || original.PrivateKeyReference != attempt.PrivateKeyReference
            || !original.OperationIds.OrderBy(item => item.Key).SequenceEqual(attempt.OperationIds.OrderBy(item => item.Key));
        if (row.Revision != expectedRevision || attempt.Revision != expectedRevision + 1 || identityChanged)
        {
            throw new InvalidOperationException("LocalAttemptRevisionConflict");
        }

        if (attempt.ReservationIndex.HasValue && attempt.ProtectedInvitation is not null)
        {
            throw new InvalidDataException("ReservedInvitationMustBeRemoved");
        }

        context.Entry(row).Property(item => item.Revision).OriginalValue = expectedRevision;
        row.Revision = attempt.Revision;
        row.Payload = JsonSerializer.Serialize(attempt);
    }
}
