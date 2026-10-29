using DotNext.Net.Cluster.Consensus.Raft;
using DotNext.Net.Cluster.Consensus.Raft.Membership;
using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace UptimeHarbor.Tests.Web.Cluster;

public class PersistentMembershipTests
{
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
