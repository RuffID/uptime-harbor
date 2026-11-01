using System.Diagnostics;
using DotNext.Net.Cluster.Consensus.Raft;
using DotNext.Net.Cluster.Consensus.Raft.Http;
using DotNext.Net.Cluster.Consensus.Raft.StateMachine;
using UptimeHarbor.Infrastructure.Cluster;

namespace UptimeHarbor.Web.Cluster;

public static class DotNextNodeServices
{
    // Создание DI не разрешает resolve WAL или Start: recovery-порядком владеет runtime.
    public static ServiceProvider Create(IServiceProvider services, ClusterStateMachine machine,
        ClusterOptions options, bool coldStart)
    {
        ServiceCollection node = new();
        node.AddSingleton(services.GetRequiredService<ILoggerFactory>());
        node.AddSingleton(_ => new DiagnosticListener("UptimeHarbor.Peer"));
        node.AddMetrics();
        node.AddSingleton(services.GetRequiredService<IHttpMessageHandlerFactory>());
        node.AddSingleton<IStateMachine>(machine);
        node.UsePersistentLog(new WriteAheadLog.Options
        {
            Location = Path.Combine(options.StoragePath, "wal"),
            FlushInterval = TimeSpan.Zero,
            FlushOnCommit = true,
            HashAlgorithm = WriteAheadLog.IntegrityHashAlgorithm.Crc64
        });
        node.UsePersistentConfigurationStorage(Path.Combine(options.StoragePath, "membership"));
        node.ConfigureLocalNode(configuration =>
        {
            configuration.PublicEndPoint = new Uri(options.PublicUri);
            configuration.ColdStart = coldStart;
            configuration.LowerElectionTimeout = 30000;
            configuration.UpperElectionTimeout = 36000;
            configuration.HeartbeatThreshold = 1D / 6D;
            configuration.RpcTimeout = TimeSpan.FromSeconds(3);
            configuration.RequestTimeout = TimeSpan.FromSeconds(10);
            configuration.AggressiveLeaderStickiness = true;
            configuration.IsLeaderLeaseEnabled = false;
            configuration.WarmupRounds = 10;
            configuration.ClientHandlerName = PeerClientHandler.CLIENT_NAME;
        });

        return node.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }

    public static RequestDelegate CreateProtocol(IServiceProvider services)
    {
        return new ApplicationBuilder(services).UseConsensusProtocolHandler().Build();
    }
}
