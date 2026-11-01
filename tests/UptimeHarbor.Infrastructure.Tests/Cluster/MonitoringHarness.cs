using Microsoft.Extensions.DependencyInjection;
using UptimeHarbor.Application.Abstractions.Persistence;
using UptimeHarbor.Application.Abstractions.UnitOfWork;
using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Infrastructure.Cluster;
using UptimeHarbor.Infrastructure.Persistence.UnitOfWork;

namespace UptimeHarbor.Tests.Infrastructure.Cluster;

internal class MonitoringHarness : IDisposable, IClusterStateView, IConsensusWriter, IRevisionTransport, IMonitoringAudit
{
    public RevisionStateStore State { get; } = new();
    public ObservationMemory Storage { get; } = new();
    public Clock Time { get; } = new();
    public NodeReadiness Readiness { get; }
    public ClusterOptions Options { get; }
    public ServiceProvider Services { get; }
    public ConsensusStatus? Consensus { get; set; }
    public AcceptedClusterState Accepted { get; set; }
    public RevisionDispatcher Dispatcher { get; }
    public RevisionApplyEngine Engine { get; }
    public Projection Store { get; } = new();
    public List<(Guid Node, Guid Event)> Audit { get; } = [];
    public bool LoseTransportResponse { get; set; }

    public MonitoringHarness()
    {
        var (image, fact) = ObservationCaptureTests.Scenario();
        State.Publish(image);
        Time.Now = fact.CheckedAtUtc;
        Options = new ClusterOptions
        {
            Mode = "Restart", ClusterId = image.ClusterId, Generation = image.Generation,
            NodeId = fact.ObserverNodeId, StorageGeneration = fact.StorageGeneration
        };
        Consensus = new(image.ClusterId, image.Generation, Options.NodeId, 1, 1, true, image.Index,
            new HashSet<Guid> { Options.NodeId }, CancellationToken.None, CancellationToken.None);
        // Здесь proof — явно управляемый test double, production dependencies не открываются.
        Accepted = new(true, true, true, true, true, image.Index, 1, Guid.NewGuid(), true);
        Readiness = new(Time);
        foreach (string component in new[] { "Database", "Storage", "Clock" })
        {
            Readiness.Report(component, true, "Ready", TimeSpan.FromHours(1));
        }

        Readiness.ConfirmRecovery(0, Accepted, Consensus);
        Services = new ServiceCollection().AddSingleton(State).AddSingleton(Storage).AddSingleton<ObservationCaptureGate>()
            .AddScoped<IObservationCaptureRepository>(_ => Storage)
            .AddScoped<IObservationCaptureUnitOfWork>(_ => new ObservationCaptureUnitOfWork(Storage, Storage))
            .AddScoped<ObservationCaptureService>().BuildServiceProvider();
        Engine = new(State, new(), Store);
        Dispatcher = new(this, State, this, this, Time);
    }

    public async ValueTask<long> ReplicateAsync(ReadOnlyMemory<byte> payload, int commandId, CancellationToken token)
    {
        long index = State.Current!.Index + 1;
        await Engine.ApplyAsync(RevisionCodec.Decode(payload.Span), index, Consensus!.Term, Options.NodeId, token);
        Accepted = Accepted with { AppliedIndex = index };
        return index;
    }

    public ValueTask<bool> ResignAsync(CancellationToken token)
    {
        throw new InvalidOperationException("Monitoring cannot initiate an election.");
    }

    public async Task<IReadOnlyList<CommandResult>> SendAsync(IReadOnlyList<RevisionCommand> commands, CancellationToken token)
    {
        List<CommandResult> results = [];
        foreach (RevisionCommand command in commands)
        {
            results.Add(await Dispatcher.AcceptAsync(command, command.OriginNodeId, token));
        }

        if (LoseTransportResponse)
        {
            throw new IOException("InjectedResponseLoss");
        }

        return results;
    }

    public void Transition(Guid nodeId, Guid incidentId, Guid eventId, Guid targetId, long acceptedIndex)
    {
        Audit.Add((nodeId, eventId));
    }

    public void Dispose()
    {
        Services.Dispose();
    }

    internal class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; }
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override DateTimeOffset GetUtcNow()
        {
            return Now;
        }

        public override long GetTimestamp()
        {
            return Now.UtcTicks;
        }
    }

    internal class Projection : IRevisionProjectionStore
    {
        public ProjectionHead? Head { get; set; }
        private readonly Dictionary<long, string> receipts = [];
        public bool Fail { get; set; }
        public List<UptimeHarbor.Application.Abstractions.Persistence.ProjectionWriteSet> Writes { get; } = [];

        public Task ApplyAsync(UptimeHarbor.Application.Abstractions.Persistence.ProjectionWriteSet changes, CancellationToken token)
        {
            if (Fail)
            {
                throw new IOException("InjectedProjectionFailure");
            }

            if (receipts.TryGetValue(changes.Receipt.LogIndex, out string? digest))
            {
                Xunit.Assert.Equal(digest, changes.Receipt.Digest);
                return Task.CompletedTask;
            }

            receipts.Add(changes.Receipt.LogIndex, changes.Receipt.Digest);
            Head = new(changes.Receipt.ClusterId, changes.Receipt.ClusterGeneration, changes.Receipt.LogIndex,
                changes.Receipt.Term, changes.Receipt.Digest);

            Writes.Add(changes);
            return Task.CompletedTask;
        }

        public Task VerifyAsync(RevisionImage image, CancellationToken token)
        {
            if (Head is not null)
            {
                Xunit.Assert.Equal(image.Index, Head.Index);
                Xunit.Assert.Equal(image.Digest, Head.Digest);
            }

            return Task.CompletedTask;
        }

        public Task<ProjectionHead?> ReadHeadAsync(CancellationToken token)
        {
            return Task.FromResult(Head);
        }

        public Task<RevisionImage> ExportAsync(RevisionImage image, CancellationToken token)
        {
            throw new NotSupportedException();
        }

        public Task InstallAsync(RevisionImage image, Guid snapshotId, string digest, CancellationToken token)
        {
            throw new NotSupportedException();
        }

        public Task CheckpointAsync(Guid snapshotId, string digest, long index, CancellationToken token)
        {
            throw new NotSupportedException();
        }
    }
}
