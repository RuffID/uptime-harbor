using UptimeHarbor.Domain.Cluster;
using UptimeHarbor.Domain.Monitoring;
using Xunit;

namespace UptimeHarbor.Tests.Domain.Cluster;

public class SharedStorageInvariantTests
{
    private static readonly DateTimeOffset NOW = new(2026, 10, 29, 0, 0, 0, TimeSpan.Zero);
    private static readonly string DIGEST = new('A', 64);
    private static readonly ClusterIdentity CLUSTER = new(Guid.NewGuid(), 1);

    [Theory]
    [InlineData(" admin ", "ADMIN")]
    [InlineData("Ａｄｍｉｎ", "ADMIN")]
    [InlineData("e\u0301", "É")]
    public void Login_UsesCanonicalUniqueKey(string login, string expected)
    {
        // Arrange
        string suppliedLogin = login;

        // Act
        string normalized = SharedAccount.NormalizeLogin(suppliedLogin);

        // Assert
        Assert.Equal(expected, normalized);
    }

    [Fact]
    public void SharedAccount_DoesNotDependOnOriginNode_AndForbidsSelfActions()
    {
        // Arrange
        SharedAccount account = Account(Guid.NewGuid());
        CommandIdentity first = new(Guid.NewGuid(), Guid.NewGuid(), account.UserId, null);
        CommandIdentity second = new(Guid.NewGuid(), Guid.NewGuid(), account.UserId, null);

        // Act
        Exception? failure = Record.Exception(() => account.EnsureCanDeactivate(account.UserId, 2));

        // Assert
        Assert.IsType<InvalidOperationException>(failure);
        Assert.Equal(first.ActorUserId, second.ActorUserId);
        Assert.NotEqual(first.OriginNodeId, second.OriginNodeId);
    }

    [Fact]
    public void OrderedDeactivations_KeepTheLastActiveAdministrator()
    {
        // Arrange
        SharedAccount first = Account(Guid.NewGuid());
        SharedAccount second = Account(Guid.NewGuid());
        Guid actor = Guid.NewGuid();

        // Act
        first.EnsureCanDeactivate(actor, 2);
        Exception? lastFailure = Record.Exception(() => second.EnsureCanDeactivate(actor, 1));

        // Assert
        Assert.IsType<InvalidOperationException>(lastFailure);
    }

    [Fact]
    public void Checks_DoNotAdvanceAcceptedConfiguration()
    {
        // Arrange
        MonitoredServer server = new(Guid.NewGuid(), "Server", "https://example.test", null, true);
        Guid generation = Guid.NewGuid();
        server.AcceptConfigurationRevision(10, generation);

        // Act
        server.RecordSuccessfulCheck("endpoint", NOW);
        server.RecordFailedCheck(NOW.AddMinutes(1), 3);

        // Assert
        Assert.Equal(10, server.ConfigRevision);
        Assert.Equal(generation, server.CheckGeneration);
        Assert.Equal(2, server.Version);
    }

    [Theory]
    [InlineData(OperationOutcome.Cancelled)]
    [InlineData(OperationOutcome.Replaced)]
    public void TerminalControl_SurvivesPendingRestoreAndLateRetry(OperationOutcome outcome)
    {
        // Arrange
        Guid operationId = Guid.NewGuid();
        Guid? replacement = null;
        if (outcome == OperationOutcome.Replaced)
        {
            replacement = Guid.NewGuid();
        }

        PendingOperation pending = new(CLUSTER,
            new CommandIdentity(operationId, Guid.NewGuid(), Guid.NewGuid(), null),
            "UpdateServer", Guid.NewGuid(), 10, 5, DIGEST, "{}", NOW);
        pending.MarkTransmissionStarted(NOW.AddMinutes(1));
        OperationDecision decision = new(operationId, DIGEST, outcome, Position(20), replacedBy: replacement);

        // Act
        pending.RequireConfirmationAfterRestore();
        pending.Reconcile(decision);
        Exception? retryFailure = Record.Exception(() => pending.MarkTransmissionStarted(NOW.AddMinutes(2)));

        // Assert
        Assert.Equal(PendingState.Resolved, pending.State);
        Assert.IsType<InvalidOperationException>(retryFailure);
        Assert.Throws<InvalidOperationException>(() => decision.EnsureSameRequest(new string('B', 64)));
    }

    [Fact]
    public void Conflict_PreservesTheFirstAcceptedVariant()
    {
        // Arrange
        ConflictRecord conflict = new(Guid.NewGuid(), Position(12), Guid.NewGuid(), Guid.NewGuid(),
            1, 10, "A", "B", "C", NOW);

        // Act
        conflict.Resolve(Guid.NewGuid(), NOW.AddMinutes(1));

        // Assert
        Assert.Equal("B", conflict.ProtectedAcceptedPayload);
        Assert.Equal("C", conflict.ProtectedProposedPayload);
        Assert.NotNull(conflict.ResolutionOperationId);
    }

    [Fact]
    public void Cursor_AllowsProvenRaftGaps_ButRejectsGenerationMixingAndStaleWriters()
    {
        // Arrange
        ProjectionCursor cursor = new(Guid.NewGuid(), CLUSTER);

        // Act
        cursor.Advance(0, Position(7), DIGEST);

        // Assert
        Assert.Equal(7, cursor.LogIndex);
        Assert.Throws<InvalidOperationException>(() => cursor.Advance(0, Position(8), DIGEST));
        Assert.Throws<InvalidOperationException>(() => cursor.Advance(7,
            new JournalPosition(new ClusterIdentity(CLUSTER.ClusterId, 2), 8, 1), DIGEST));
    }

    [Fact]
    public void Capture_AllocatesStableSequence_AndRejectsInvalidPayloadBeforeAdvancing()
    {
        // Arrange
        ObservationCaptureCursor stream = new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

        // Act
        CapturedObservation first = stream.Capture(Guid.NewGuid(), DIGEST, "{}", NOW);
        Exception? invalid = Record.Exception(() => stream.Capture(Guid.NewGuid(), DIGEST, "", NOW));

        // Assert
        Assert.Equal(1, first.Sequence);
        Assert.Equal(1, stream.LastSequence);
        Assert.IsType<ArgumentException>(invalid);
    }

    [Fact]
    public void Retention_UsesCompletionYearAndProtectsUnfinishedDependencies()
    {
        // Arrange
        DateTimeOffset completion = new(2024, 2, 29, 0, 0, 0, TimeSpan.Zero);
        DateTimeOffset boundary = new(2025, 2, 28, 0, 0, 0, TimeSpan.Zero);

        // Act
        bool before = HistoryRetention.CanRemoveDetails(completion, boundary.AddTicks(-1), HistoryProtection.None);
        bool atBoundary = HistoryRetention.CanRemoveDetails(completion, boundary, HistoryProtection.None);

        // Assert
        Assert.False(before);
        Assert.True(atBoundary);
        Assert.False(HistoryRetention.CanRemoveDetails(null, NOW, HistoryProtection.None));
        foreach (HistoryProtection protection in Enum.GetValues<HistoryProtection>().Where(value => value != HistoryProtection.None))
        {
            Assert.False(HistoryRetention.CanRemoveDetails(completion, NOW, protection));
        }
    }

    [Fact]
    public void KnownSuccess_CannotBeRetriedOrRolledBackByLateFailure()
    {
        // Arrange
        DeliveryIntent intent = Intent();
        Guid attemptId = Guid.NewGuid();
        intent.BeginAttempt(attemptId);

        // Act
        intent.RecordResult(attemptId, true, NOW, null);
        intent.RecordResult(attemptId, false, NOW.AddMinutes(1), NOW.AddMinutes(2));

        // Assert
        Assert.Equal(DeliveryState.Succeeded, intent.State);
        Assert.Throws<InvalidOperationException>(() => intent.BeginAttempt(Guid.NewGuid()));
    }

    [Fact]
    public void Summary_SuppressesOriginalIntent_AndRecoveryIdentityRemainsStableAfterUnbind()
    {
        // Arrange
        DeliveryIntent intent = Intent();
        RecoveryEntitlement entitlement = new(Guid.NewGuid(), Position(10), intent.IncidentId!.Value, intent.ChatId, intent.Id);
        Guid summaryId = Guid.NewGuid();
        Guid recoveryId = Guid.NewGuid();

        // Act
        intent.SuppressBySummary(summaryId);
        entitlement.AssignRecovery(recoveryId);
        entitlement.AssignRecovery(recoveryId);
        entitlement.Consume(NOW);

        // Assert
        Assert.Equal(DeliveryState.SuppressedBySummary, intent.State);
        Assert.Equal(summaryId, intent.SummaryId);
        Assert.Throws<InvalidOperationException>(() => intent.BeginAttempt(Guid.NewGuid()));
        Assert.Throws<InvalidOperationException>(() => entitlement.AssignRecovery(Guid.NewGuid()));
        Assert.Equal(recoveryId, entitlement.RecoveryIntentId);
    }

    private static SharedAccount Account(Guid userId)
    {
        return new SharedAccount(userId, Guid.NewGuid(), "admin",
            new ProtectedCredential("protected-hash", 1, "security", "concurrency"), true, 1);
    }

    [Fact]
    public void LateSuccess_RequiresAnAcceptedAttempt_AndSurvivesAChangedSender()
    {
        // Arrange
        DeliveryIntent intent = Intent();
        Guid firstAttemptId = Guid.NewGuid();
        DeliveryAttempt firstAttempt = new(firstAttemptId, Position(11), intent.Id, 1,
            Guid.NewGuid(), Guid.NewGuid(), NOW);
        intent.BeginAttempt(firstAttemptId);
        intent.RecordResult(firstAttemptId, false, NOW, NOW.AddSeconds(5));
        intent.BeginAttempt(Guid.NewGuid());
        firstAttempt.RecordResult(AttemptOutcome.Succeeded, NOW.AddSeconds(6), 123, null);

        // Act
        intent.AcceptLateSuccess(firstAttempt);

        // Assert
        Assert.Equal(DeliveryState.Succeeded, intent.State);
        Assert.Equal(2, intent.AttemptCount);
        Assert.Throws<InvalidOperationException>(() => intent.BeginAttempt(Guid.NewGuid()));
    }

    private static JournalPosition Position(long index)
    {
        return new JournalPosition(CLUSTER, index, 1);
    }

    private static DeliveryIntent Intent()
    {
        return new DeliveryIntent(Guid.NewGuid(), Position(10), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            DeliveryKind.Problem, Guid.NewGuid(), Guid.NewGuid().ToString("N"), "{}");
    }
}
