using DotNext.Net.Cluster.Consensus.Raft;
using DotNext.Net.Cluster.Consensus.Raft.Membership;
using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.DependencyInjection;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Web.Cluster;
using Xunit;

namespace UptimeHarbor.Tests.Web.Cluster;

public class PersistentMembershipTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public async Task EmptyJoinSeedsExactSnapshotVotersAndCannotOverwriteExistingMembership(int count)
    {
        // Arrange
        string directory = Path.Combine(Path.GetTempPath(), "UptimeHarbor-membership-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            await using ServiceProvider services = Services(Path.Combine(directory, "membership"));
            var storage = services.GetRequiredService<IClusterConfigurationStorage<UriEndPoint>>();
            ClusterOperationsImage operations = new() { MembershipVersion = 7 };
            for (int number = 0; number < count; number++)
            {
                Guid id = Guid.NewGuid();
                operations = operations with { Nodes = operations.Nodes.Add(id,
                    new(id, Guid.NewGuid(), $"https://node{number}.invalid/internal/raft", "transport", true, false, false, 0)) };
            }
            Guid joining = Guid.NewGuid();
            operations = operations with { Nodes = operations.Nodes.Add(joining,
                new(joining, Guid.NewGuid(), "https://joining.invalid/internal/raft", "joining", false, false, false, 0)) };
            RevisionImage snapshot = new(Guid.NewGuid(), 1) { Operations = operations };

            // Act
            await ClusterJoinMembership.PrepareAsync(storage, snapshot, TestContext.Current.CancellationToken);
            var saved = await ((IClusterConfigurationStorage)storage).LoadConfigurationAsync(TestContext.Current.CancellationToken);
            var members = await storage.LoadConfigurationAsync(TestContext.Current.CancellationToken);

            // Assert
            Assert.Equal(7, saved.Version);
            Assert.Equal(count, members.Members.Count);
            Assert.DoesNotContain(new UriEndPoint(new Uri(operations.Nodes[joining].Address)), members.Members);
            await Assert.ThrowsAsync<InvalidDataException>(() => ClusterJoinMembership.PrepareAsync(storage,
                snapshot, TestContext.Current.CancellationToken));
        }
        finally
        {
            if (Path.GetDirectoryName(directory) != Path.TrimEndingDirectorySeparator(Path.GetTempPath())
                || !Path.GetFileName(directory).StartsWith("UptimeHarbor-membership-", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Unsafe test cleanup path.");
            }
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public async Task RealDotNextConfigurationKeepsAcceptedVotersAfterReopen(int count)
    {
        // Arrange
        string directory = Path.Combine(Path.GetTempPath(), "UptimeHarbor-membership-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "membership");
        UriEndPoint[] voters = Enumerable.Range(0, count)
            .Select(index => new UriEndPoint(new Uri($"https://node{index}.example/internal/raft"))).ToArray();
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        try
        {
            // Act
            await using (ServiceProvider first = Services(path))
            {
                IClusterConfigurationStorage<UriEndPoint> storage = first.GetRequiredService<IClusterConfigurationStorage<UriEndPoint>>();
                IClusterConfiguration<UriEndPoint> updated = await storage.LoadConfigurationAsync(cancellation);
                foreach (UriEndPoint voter in voters)
                {
                    updated = updated.Add(voter);
                }

                await ((IClusterConfigurationStorage)storage).SaveConfigurationAsync(updated, 7, cancellation);
            }

            await using ServiceProvider reopened = Services(path);
            IClusterConfigurationStorage<UriEndPoint> restored = reopened.GetRequiredService<IClusterConfigurationStorage<UriEndPoint>>();
            var saved = await ((IClusterConfigurationStorage)restored).LoadConfigurationAsync(cancellation);
            IClusterConfiguration<UriEndPoint> configuration = await restored.LoadConfigurationAsync(cancellation);

            // Assert
            Assert.Equal(7, saved.Version);
            Assert.Equal(count, configuration.Members.Count);
            Assert.Equal(count / 2 + 1, configuration.Members.Count / 2 + 1);
            Assert.All(voters, voter => Assert.Contains(voter, configuration.Members));
        }
        finally
        {
            // Каталог создан этим тестом; не затрагивает provisioned storage.
            if (Path.GetDirectoryName(directory) != Path.TrimEndingDirectorySeparator(Path.GetTempPath())
                || !Path.GetFileName(directory).StartsWith("UptimeHarbor-membership-", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Unsafe test cleanup path.");
            }

            Directory.Delete(directory, recursive: true);
        }
    }

    private static ServiceProvider Services(string path)
    {
        ServiceCollection services = new();
        services.UsePersistentConfigurationStorage(path);
        return services.BuildServiceProvider();
    }
}
