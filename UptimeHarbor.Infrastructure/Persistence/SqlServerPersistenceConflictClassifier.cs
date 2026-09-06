using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using UptimeHarbor.Application.Abstractions.UnitOfWork;
using UptimeHarbor.Domain.Monitoring;
using UptimeHarbor.Domain.Notifications;

namespace UptimeHarbor.Infrastructure.Persistence;

internal static class SqlServerPersistenceConflictClassifier
{
    internal const int DUPLICATE_KEY_ERROR = 2601;
    internal const int UNIQUE_CONSTRAINT_ERROR = 2627;

    public static bool TryClassify(
        DbUpdateException exception,
        out PersistenceConflictKind kind)
    {
        ArgumentNullException.ThrowIfNull(exception);
        SqlException? sqlException = FindSqlException(exception);
        if (sqlException is null)
        {
            kind = default;
            return false;
        }

        int[] relevantNumbers = sqlException.Errors
            .Cast<SqlError>()
            .Select(error => error.Number)
            .Where(IsUniqueViolationNumber)
            .Distinct()
            .ToArray();
        if (relevantNumbers.Length == 0)
        {
            kind = default;
            return false;
        }

        Type[] entityTypes = exception.Entries
            .Select(entry => entry.Entity.GetType())
            .Distinct()
            .ToArray();
        return TryClassify(relevantNumbers[0], entityTypes, out kind);
    }

    internal static bool TryClassify(
        int sqlErrorNumber,
        IReadOnlyCollection<Type> entityTypes,
        out PersistenceConflictKind kind)
    {
        ArgumentNullException.ThrowIfNull(entityTypes);
        if (!IsUniqueViolationNumber(sqlErrorNumber) || entityTypes.Count != 1)
        {
            kind = default;
            return false;
        }

        Type entityType = entityTypes.Single();
        if (typeof(MonitoredServer).IsAssignableFrom(entityType))
        {
            kind = PersistenceConflictKind.DuplicateServerName;
            return true;
        }

        if (typeof(TelegramChat).IsAssignableFrom(entityType))
        {
            kind = PersistenceConflictKind.DuplicateTelegramChatId;
            return true;
        }

        if (typeof(NotificationBinding).IsAssignableFrom(entityType))
        {
            kind = PersistenceConflictKind.DuplicateNotificationBinding;
            return true;
        }

        kind = default;
        return false;
    }

    private static bool IsUniqueViolationNumber(int number)
    {
        return number is DUPLICATE_KEY_ERROR or UNIQUE_CONSTRAINT_ERROR;
    }

    private static SqlException? FindSqlException(Exception exception)
    {
        Exception? current = exception;
        while (current is not null)
        {
            if (current is SqlException sqlException)
            {
                return sqlException;
            }

            current = current.InnerException;
        }

        return null;
    }
}
