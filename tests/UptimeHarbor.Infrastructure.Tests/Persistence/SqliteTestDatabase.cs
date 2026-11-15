using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using UptimeHarbor.Infrastructure.Persistence;

namespace UptimeHarbor.Tests.Infrastructure.Persistence;

/// <summary>SQLite-модель для provider-тестов с сохранением ограничения срока issuer.</summary>
internal static class SqliteTestDatabase
{
    public static DbContextOptions<UptimeHarborDbContext> CreateOptions(SqliteConnection connection)
    {
        DbContextOptionsBuilder<UptimeHarborDbContext> builder = new();
        Configure(builder, connection);

        return builder.Options;
    }

    public static void Configure(DbContextOptionsBuilder builder, SqliteConnection connection)
    {
        // DateTime.AddYears сохраняет точность ticks и перенос 29 февраля так же, как SQL Server DATEADD.
        connection.CreateFunction<DateTime, DateTime, bool>("enrollment_issuer_lifetime_valid",
            (notBefore, notAfter) => notAfter > notBefore && notAfter <= notBefore.AddYears(3),
            isDeterministic: true);
        // В модели DATALENGTH применяется только к binary DER, где длина равна числу байтов.
        connection.CreateFunction<byte[]?, int?>("DATALENGTH", value =>
        {
            if (value is null)
            {
                return null;
            }

            return value.Length;
        }, isDeterministic: true);
        builder.UseSqlite(connection).ReplaceService<IModelCustomizer, SqliteTestModelCustomizer>();
    }
}
