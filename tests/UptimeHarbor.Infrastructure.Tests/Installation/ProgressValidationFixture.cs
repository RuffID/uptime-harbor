using System.Collections;
using System.Collections.Immutable;
using System.Linq.Expressions;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Installation;
using UptimeHarbor.Domain.Cluster;
using UptimeHarbor.Infrastructure.Installation;
using UptimeHarbor.Infrastructure.Persistence;

namespace UptimeHarbor.Tests.Infrastructure.Installation;

/// <summary>Выполняет запросы production store над памятью без EF provider или соединения с БД.</summary>
internal sealed class ProgressValidationFixture : IDisposable
{
    public LocalInstallationProfile Profile { get; set; }
    public List<LocalStepRecord> Steps { get; } = [];
    public List<LocalAttemptRecord> Attempts { get; } = [];
    public List<int> PageSizes { get; } = [];
    public List<Guid> ReadIds { get; } = [];
    public List<Expression> StepQueries { get; } = [];
    public Action<int>? OnStepPage { get; set; }
    public LocalSetupAccountStore Store { get; }
    private readonly MemoryContext context;

    public ProgressValidationFixture(int count)
    {
        DateTimeOffset now = new(2026, 11, 15, 0, 0, 0, TimeSpan.Zero);
        Guid clusterId = Guid.NewGuid();
        Guid attemptId = Guid.NewGuid();
        Guid nodeId = Guid.NewGuid();
        Guid storageGeneration = Guid.NewGuid();
        Profile = new(Guid.NewGuid(), "Bootstrap", InstallationPhase.ClusterReady, 100, 10, 1,
            Guid.NewGuid(), now.AddMinutes(2), AttemptId: attemptId, NodeId: nodeId,
            StorageGeneration: storageGeneration, KeyReference: "peer-key", ClusterId: clusterId, ClusterGeneration: 1);
        for (int index = 0; index < count; index++)
        {
            Guid id = Guid.Parse($"00000000-0000-0000-0000-{index + 1:X12}");
            RevisionCommand command = new()
            {
                Schema = 3, ClusterId = clusterId, ClusterGeneration = 1,
                OperationId = id, OriginNodeId = nodeId, EntityId = attemptId,
                ActorUserId = Guid.Empty, ActorRevision = 0,
                EntityKind = DeletedEntityKind.Node, Action = "RecordOnboardingProgress", BaseRevision = 0,
                CreatedAtUtc = now, SystemSource = "OnboardingCoordinator",
                Onboarding = new()
                {
                    StorageGeneration = storageGeneration, InvitationId = attemptId,
                    AttemptId = attemptId, BootId = Profile.BootId,
                    BootChallenge = Convert.ToBase64String(new byte[32]), ProcessEpoch = 1, ProgressIndex = index
                }
            };
            LocalAttemptStep step = new(id, attemptId, "Enrollment/" + command.Action,
                RevisionCodec.Digest(command), "AcceptedEnrollment", 100, 10, now, JsonSerializer.Serialize(command));
            Steps.Add(new() { Id = id, AttemptId = step.AttemptId, Payload = JsonSerializer.Serialize(step) });
        }

        context = new(this);
        Store = new(context);
    }

    public void AddAttempt()
    {
        EnrollmentRequest request = new(1, Profile.ClusterId!.Value, 1, Guid.NewGuid(), Profile.AttemptId!.Value,
            Profile.NodeId!.Value, Profile.StorageGeneration!.Value, "https://peer.example:8443/internal/raft",
            new string('A', 64), new string('B', 64));
        LocalOnboardingAttempt attempt = new(Profile.AttemptId.Value, request, new string('C', 64),
            Profile.KeyReference!, 1, ImmutableDictionary<string, Guid>.Empty, null, null);
        Attempts.Add(new() { Id = attempt.AttemptId, Revision = attempt.Revision, Payload = JsonSerializer.Serialize(attempt) });
    }

    public void ChangeStep(int index, Func<LocalAttemptStep, LocalAttemptStep> change)
    {
        LocalStepRecord row = Steps[index];
        LocalAttemptStep step = JsonSerializer.Deserialize<LocalAttemptStep>(row.Payload)!;
        row.Payload = JsonSerializer.Serialize(change(step));
    }

    public string TranslateStepQuery(int page, UptimeHarborDbContext sqlContext)
    {
        IQueryable<LocalStepRecord> target = sqlContext.Set<LocalStepRecord>();
        Expression expression = new SqlQueryRoot(target.Expression).Visit(StepQueries[page]);
        return target.Provider.CreateQuery<LocalStepRecord>(expression).ToQueryString();
    }

    public void Dispose()
    {
        context.Dispose();
    }

    private sealed class MemoryContext(ProgressValidationFixture owner)
        : UptimeHarborDbContext(new DbContextOptionsBuilder<UptimeHarborDbContext>().UseSqlServer().Options)
    {
        public override DbSet<TEntity> Set<TEntity>()
        {
            if (typeof(TEntity) == typeof(LocalStepRecord))
            {
                return (DbSet<TEntity>)(object)new MemorySet<LocalStepRecord>(owner.Steps, owner);
            }

            if (typeof(TEntity) == typeof(LocalAttemptRecord))
            {
                return (DbSet<TEntity>)(object)new MemorySet<LocalAttemptRecord>(owner.Attempts, owner);
            }

            throw new NotSupportedException(typeof(TEntity).Name);
        }
    }

    private sealed class MemorySet<T>(IEnumerable<T> rows, ProgressValidationFixture owner)
        : DbSet<T>, IQueryable<T>, IAsyncEnumerable<T> where T : class
    {
        private readonly MemoryQuery<T> query = new(rows, owner);
        public override IEntityType EntityType
        {
            get
            {
                throw new NotSupportedException();
            }
        }

        Type IQueryable.ElementType => typeof(T);
        Expression IQueryable.Expression => ((IQueryable<T>)query).Expression;
        IQueryProvider IQueryable.Provider => ((IQueryable<T>)query).Provider;

        public override IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken token = default)
        {
            return query.GetAsyncEnumerator(token);
        }

        IEnumerator<T> IEnumerable<T>.GetEnumerator()
        {
            return ((IEnumerable<T>)query).GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return ((IEnumerable<T>)query).GetEnumerator();
        }
    }

    private sealed class MemoryQuery<T> : EnumerableQuery<T>, IOrderedQueryable<T>, IAsyncEnumerable<T>
    {
        private readonly ProgressValidationFixture owner;
        IQueryProvider IQueryable.Provider => new MemoryProvider(this, owner);

        public MemoryQuery(IEnumerable<T> rows, ProgressValidationFixture owner) : base(rows)
        {
            this.owner = owner;
        }

        public MemoryQuery(Expression expression, ProgressValidationFixture owner) : base(expression)
        {
            this.owner = owner;
        }

        public IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken token = default)
        {
            int page = -1;
            if (typeof(T) == typeof(LocalStepRecord))
            {
                page = owner.PageSizes.Count;
                owner.PageSizes.Add(0);
                owner.StepQueries.Add(((IQueryable<T>)this).Expression);
                owner.OnStepPage?.Invoke(page);
            }

            return new MemoryEnumerator<T>(((IEnumerable<T>)this).GetEnumerator(), owner, page, token);
        }
    }

    private sealed class MemoryProvider(IQueryProvider inner, ProgressValidationFixture owner) : IQueryProvider
    {
        public IQueryable CreateQuery(Expression expression)
        {
            throw new NotSupportedException();
        }

        public IQueryable<TElement> CreateQuery<TElement>(Expression expression)
        {
            return new MemoryQuery<TElement>(expression, owner);
        }

        public object? Execute(Expression expression)
        {
            return inner.Execute(expression);
        }

        public TResult Execute<TResult>(Expression expression)
        {
            return inner.Execute<TResult>(expression);
        }
    }

    private sealed class MemoryEnumerator<T>(IEnumerator<T> inner, ProgressValidationFixture owner,
        int page, CancellationToken token) : IAsyncEnumerator<T>
    {
        public T Current => inner.Current;

        public ValueTask<bool> MoveNextAsync()
        {
            token.ThrowIfCancellationRequested();
            bool next = inner.MoveNext();
            if (next && page >= 0)
            {
                owner.PageSizes[page]++;
                owner.ReadIds.Add(((LocalStepRecord)(object)inner.Current!).Id);
            }

            return ValueTask.FromResult(next);
        }

        public ValueTask DisposeAsync()
        {
            inner.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class SqlQueryRoot(Expression target) : ExpressionVisitor
    {
        protected override Expression VisitConstant(ConstantExpression node)
        {
            if (node.Value is IQueryable<LocalStepRecord>)
            {
                return target;
            }

            return base.VisitConstant(node);
        }
    }
}
