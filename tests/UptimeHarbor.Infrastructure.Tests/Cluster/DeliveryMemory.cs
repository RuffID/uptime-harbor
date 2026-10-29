using System.Collections.Concurrent;
using UptimeHarbor.Application.Abstractions.Persistence;
using UptimeHarbor.Application.Abstractions.UnitOfWork;
using UptimeHarbor.Domain.Cluster;

namespace UptimeHarbor.Tests.Infrastructure.Cluster;

internal class DeliveryMemory
{
    public ConcurrentDictionary<Guid, CapturedDeliveryResult> Saved { get; } = new();
    public bool LoseCommitResponse { get; set; }

    public IDeliveryResultUnitOfWork CreateWork()
    {
        return new Work(this);
    }

    private class Work(DeliveryMemory owner) : IDeliveryResultUnitOfWork, IDeliveryResultRepository
    {
        private CapturedDeliveryResult? staged;
        public IDeliveryResultRepository Results => this;

        public Task<CapturedDeliveryResult?> GetAsync(Guid attemptId, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(owner.Saved.GetValueOrDefault(attemptId));
        }

        public void Create(CapturedDeliveryResult result)
        {
            staged = result;
        }

        public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(1);
        }

        public async Task ExecuteInTransactionAsync(Func<CancellationToken, Task> action, CancellationToken cancellationToken = default)
        {
            await action(cancellationToken);
            if (staged is not null)
            {
                owner.Saved.TryAdd(staged.Id, staged);
            }

            if (owner.LoseCommitResponse)
            {
                throw new IOException("InjectedCommitResponseLoss");
            }
        }
    }
}
