using Microsoft.Extensions.Logging;
using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Domain.Cluster;
using UptimeHarbor.Infrastructure.Cluster.Onboarding;
using Xunit;

namespace UptimeHarbor.Tests.Infrastructure.Cluster.Renewal;

public class VoterCredentialAuditTests
{
    [Fact]
    public void DurableTransitionsLogOnceWithSystemSourceAndWithoutArtifacts()
    {
        // Arrange
        using VoterCredentialCryptoScenario scenario = new();
        CapturedLog logger = new();
        OnboardingAudit audit = new(logger);

        foreach (string action in new[] { "AuthorizeVoterCredential", "BindVoterCredential", "RetireOldCredential" })
        {
            RevisionImage before = scenario.State;
            RevisionCommand command = scenario.Commit(action);
            CommandResult result = scenario.State.Results[command.OperationId];

            // Act
            audit.Accepted(before, scenario.State, command, result);
            audit.Accepted(scenario.State, scenario.State, command, result);

            // Assert
            string message = logger.Messages.Last();
            Assert.Contains(command.OperationId.ToString(), message);
            Assert.Contains("VoterCredentialLifecycle", message);
            Assert.DoesNotContain(Convert.ToBase64String(scenario.Previous.RawData), message);
            Assert.DoesNotContain(Convert.ToBase64String(scenario.Next.RawData), message);
            Assert.DoesNotContain(scenario.Intent.PreviousFingerprint, message);
            Assert.DoesNotContain(scenario.Intent.PreviousSpkiDigest, message);
            Assert.DoesNotContain(new string('E', 64), message);
        }

        Assert.Equal(3, logger.Messages.Count);
        Assert.Contains("CertificateAuthorized", logger.Messages[0]);
        Assert.Contains("CertificateBound", logger.Messages[1]);
        Assert.Contains("CertificateRetired", logger.Messages[2]);
    }

    [Fact]
    public void RejectedAndUnconfirmedResultsCannotLogSuccess()
    {
        // Arrange
        using VoterCredentialCryptoScenario scenario = new();
        CapturedLog logger = new();
        OnboardingAudit audit = new(logger);
        RevisionImage before = scenario.State;
        RevisionCommand command = scenario.Command("BindVoterCredential");
        CommandResult rejected = scenario.Apply(command);
        Assert.Equal(OperationOutcome.Rejected, rejected.Outcome);
        RevisionCommand unknown = scenario.Command("AuthorizeVoterCredential");
        CommandResult invented = new(unknown.OperationId, RevisionCodec.Digest(unknown), OperationOutcome.Committed,
            scenario.State.Index, scenario.State.Term, scenario.State.Index, null, null, null, 0);

        // Act
        audit.Accepted(before, scenario.State, command, rejected);
        Exception? failure = Record.Exception(() => audit.Accepted(before, scenario.State, unknown, invented));

        // Assert
        Assert.IsType<InvalidDataException>(failure);
        Assert.Contains("CertificateRejected", Assert.Single(logger.Messages));
        Assert.DoesNotContain("CertificateBound", logger.Messages[0]);
    }

    private class CapturedLog : ILogger<OnboardingAudit>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull
        {
            return null;
        }

        public bool IsEnabled(LogLevel level)
        {
            return true;
        }

        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? error, Func<TState, Exception?, string> formatter)
        {
            Messages.Add(formatter(state, error));
        }
    }
}
