using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using UptimeHarbor.Infrastructure.Persistence;

namespace UptimeHarbor.Infrastructure.Telegram;

public class NotificationOutboxStore(
    UptimeHarborDbContext dbContext,
    TimeProvider timeProvider)
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

    public async Task<bool> ProcessClaimedAsync(
        Guid jobId,
        TimeSpan leaseDuration,
        int maximumAttempts,
        TimeSpan retryDelay,
        Func<CancellationToken, Task<bool>> process,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(process);
        DateTimeOffset startedAtUtc = timeProvider.GetUtcNow();
        await using IDbContextTransaction transaction =
            await dbContext.Database.BeginTransactionAsync(cancellationToken);

        int acquired = await dbContext.NotificationOutboxJobs
            .Where(job => job.Id == jobId
                && job.CompletedAtUtc == null
                && job.FailedAtUtc == null)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(
                    job => job.LockedUntilUtc,
                    startedAtUtc.Add(leaseDuration)),
                cancellationToken);
        if (acquired == 0)
        {
            await transaction.CommitAsync(cancellationToken);
            return false;
        }

        bool succeeded = await process(cancellationToken);
        DateTimeOffset completedAtUtc = timeProvider.GetUtcNow();
        NotificationOutboxJob job = await GetRequiredJobAsync(jobId, cancellationToken);
        if (succeeded)
        {
            job.Complete(completedAtUtc);
        }
        else if (job.AttemptCount >= maximumAttempts)
        {
            job.Fail(completedAtUtc);
        }
        else
        {
            job.ScheduleRetry(completedAtUtc, retryDelay);
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
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
