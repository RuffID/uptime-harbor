using Microsoft.EntityFrameworkCore;
using UptimeHarbor.Infrastructure.Persistence;

namespace UptimeHarbor.Infrastructure.Telegram;

public class NotificationOutboxStore(UptimeHarborDbContext dbContext)
{
    public async Task<IReadOnlyList<ClaimedNotificationOutboxJob>> ClaimDueAsync(
        DateTimeOffset nowUtc,
        int batchSize,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default)
    {
        if (batchSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(batchSize));
        }

        List<NotificationOutboxJob> jobs = await dbContext.NotificationOutboxJobs
            .Where(job => job.CompletedAtUtc == null
                && job.FailedAtUtc == null
                && job.NextAttemptAtUtc <= nowUtc
                && (job.LockedUntilUtc == null || job.LockedUntilUtc <= nowUtc))
            .OrderBy(job => job.NextAttemptAtUtc)
            .ThenBy(job => job.CreatedAtUtc)
            .Take(batchSize)
            .ToListAsync(cancellationToken);
        foreach (NotificationOutboxJob job in jobs)
        {
            job.StartAttempt(nowUtc, leaseDuration);
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        return jobs
            .Select(job => new ClaimedNotificationOutboxJob(
                job.Id,
                job.AttemptCount,
                job.ReadEvent()))
            .ToList();
    }

    public async Task CompleteAsync(
        Guid jobId,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default)
    {
        NotificationOutboxJob job = await GetRequiredJobAsync(jobId, cancellationToken);
        job.Complete(nowUtc);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task RetryOrFailAsync(
        Guid jobId,
        DateTimeOffset nowUtc,
        int maximumAttempts,
        TimeSpan retryDelay,
        CancellationToken cancellationToken = default)
    {
        NotificationOutboxJob job = await GetRequiredJobAsync(jobId, cancellationToken);
        if (job.AttemptCount >= maximumAttempts)
        {
            job.Fail(nowUtc);
        }
        else
        {
            job.ScheduleRetry(nowUtc, retryDelay);
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task<NotificationOutboxJob> GetRequiredJobAsync(
        Guid jobId,
        CancellationToken cancellationToken)
    {
        return await dbContext.NotificationOutboxJobs.SingleOrDefaultAsync(
                job => job.Id == jobId,
                cancellationToken)
            ?? throw new InvalidOperationException("Outbox job не найден.");
    }
}
