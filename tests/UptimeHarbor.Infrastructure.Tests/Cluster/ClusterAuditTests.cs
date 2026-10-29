using Microsoft.Extensions.Logging;
using UptimeHarbor.Infrastructure.Cluster;
using Xunit;

namespace UptimeHarbor.Tests.Infrastructure.Cluster;

public class ClusterAuditTests
{
    [Fact]
    public void AuditContainsOnlyTrustedIdentifiersAndWhitelistedOutcome()
    {
        // Arrange
        CollectingLogger log = new();
        ClusterAudit audit = new(log);
        Guid actor = Guid.NewGuid();
        Guid node = Guid.NewGuid();
        Guid change = Guid.NewGuid();

        // Act
        audit.Membership(actor, node, change, "Denied");
        ArgumentException error = Assert.Throws<ArgumentException>(() =>
            audit.Membership(actor, node, change, "secret-payload"));

        // Assert
        string message = Assert.Single(log.Messages);
        Assert.Contains(actor.ToString(), message);
        Assert.Contains(node.ToString(), message);
        Assert.Contains(change.ToString(), message);
        Assert.Contains("Denied", message);
        Assert.DoesNotContain("Committed", message);
        Assert.DoesNotContain("secret-payload", error.ToString());
    }

    private class CollectingLogger : ILogger<ClusterAudit>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull
        {
            return null;
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return true;
        }

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Messages.Add(formatter(state, exception));
        }
    }
}
