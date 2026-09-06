using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using UptimeHarbor.Application.Abstractions.UnitOfWork;
using UptimeHarbor.Domain.Monitoring;
using UptimeHarbor.Domain.Notifications;
using UptimeHarbor.Infrastructure.Persistence;
using UptimeHarbor.Infrastructure.Persistence.UnitOfWork;
using Xunit;

namespace UptimeHarbor.Tests.Infrastructure.Persistence;

public class SqlServerPersistenceConflictClassifierTests
{
    [Theory]
    [InlineData(SqlServerPersistenceConflictClassifier.DUPLICATE_KEY_ERROR)]
    [InlineData(SqlServerPersistenceConflictClassifier.UNIQUE_CONSTRAINT_ERROR)]
    public void ExpectedSqlServerNameUniqueViolation_IsClassified(int errorNumber)
    {
        bool classified = SqlServerPersistenceConflictClassifier.TryClassify(
            errorNumber,
            [typeof(MonitoredServer)],
            out PersistenceConflictKind kind);

        Assert.True(classified);
        Assert.Equal(PersistenceConflictKind.DuplicateServerName, kind);
    }

    [Theory]
    [InlineData(SqlServerPersistenceConflictClassifier.DUPLICATE_KEY_ERROR)]
    [InlineData(SqlServerPersistenceConflictClassifier.UNIQUE_CONSTRAINT_ERROR)]
    public void ExpectedSqlServerChatUniqueViolation_IsClassified(int errorNumber)
    {
        bool classified = SqlServerPersistenceConflictClassifier.TryClassify(
            errorNumber,
            [typeof(TelegramChat)],
            out PersistenceConflictKind kind);

        Assert.True(classified);
        Assert.Equal(PersistenceConflictKind.DuplicateTelegramChatId, kind);
    }

    [Theory]
    [InlineData(SqlServerPersistenceConflictClassifier.DUPLICATE_KEY_ERROR)]
    [InlineData(SqlServerPersistenceConflictClassifier.UNIQUE_CONSTRAINT_ERROR)]
    public void ExpectedSqlServerBindingUniqueViolation_IsClassified(int errorNumber)
    {
        bool classified = SqlServerPersistenceConflictClassifier.TryClassify(
            errorNumber,
            [typeof(NotificationBinding)],
            out PersistenceConflictKind kind);

        Assert.True(classified);
        Assert.Equal(PersistenceConflictKind.DuplicateNotificationBinding, kind);
    }

    [Fact]
    public void UnknownSqlServerErrorNumber_IsNotClassified()
    {
        bool classified = SqlServerPersistenceConflictClassifier.TryClassify(
            547,
            [typeof(TelegramChat)],
            out _);

        Assert.False(classified);
    }

    [Fact]
    public void UniqueViolationForUnknownEntity_IsNotClassified()
    {
        bool classified = SqlServerPersistenceConflictClassifier.TryClassify(
            SqlServerPersistenceConflictClassifier.UNIQUE_CONSTRAINT_ERROR,
            [typeof(string)],
            out _);

        Assert.False(classified);
    }

    [Fact]
    public async Task UnknownDbUpdateException_IsPropagatedUnchanged()
    {
        DbUpdateException exception = new("Unexpected provider failure.");
        TestUnitOfWork unitOfWork = new(new ThrowingSession(exception));

        DbUpdateException thrown = await Assert.ThrowsAsync<DbUpdateException>(() =>
            unitOfWork.SaveChangesAsync(TestContext.Current.CancellationToken));

        Assert.Same(exception, thrown);
    }

    private class TestUnitOfWork(IUptimeHarborDbContextSession session)
        : UptimeHarborDbUnitOfWork(session);

    private class ThrowingSession(DbUpdateException exception) : IUptimeHarborDbContextSession
    {
        public Task<IDbContextTransaction> BeginTransactionAsync(
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<int> SaveChangesAsync(CancellationToken cancellationToken) =>
            Task.FromException<int>(exception);

        public void Clear()
        {
        }
    }
}
