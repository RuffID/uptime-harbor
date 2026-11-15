using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using UptimeHarbor.Domain.Cluster;
using UptimeHarbor.Infrastructure.Cluster.Onboarding;
using UptimeHarbor.Infrastructure.Persistence;
using Xunit;

namespace UptimeHarbor.Tests.Infrastructure.Persistence;

public class SqliteEnrollmentConstraintTests
{
    [Theory]
    [InlineData("2026-11-03T00:00:00Z", "2029-11-03T00:00:00Z", true)]
    [InlineData("2026-11-03T00:00:00Z", "2029-11-03T00:00:00.0000001Z", false)]
    [InlineData("2026-11-03T00:00:00Z", "2026-11-03T00:00:00.0000001Z", true)]
    [InlineData("2026-11-03T00:00:00Z", "2026-11-03T00:00:00Z", false)]
    [InlineData("2026-11-03T00:00:00Z", "2026-11-02T23:59:59.9999999Z", false)]
    [InlineData("2024-02-29T12:34:56.1234567Z", "2027-02-28T12:34:56.1234567Z", true)]
    [InlineData("2024-02-29T12:34:56.1234567Z", "2027-02-28T12:34:56.1234568Z", false)]
    [InlineData("2024-02-29T12:34:56.1234567Z", "2027-03-01T12:34:56.1234567Z", false)]
    public async Task SQLitePreservesIssuerLifetimeConstraintIncludingLeapDayAndTicks(
        string start, string end, bool allowed)
    {
        // Arrange
        CancellationToken token = TestContext.Current.CancellationToken;
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync(token);
        await using UptimeHarborDbContext context = new(SqliteTestDatabase.CreateOptions(connection));
        await context.Database.EnsureCreatedAsync(token);
        DateTimeOffset notBefore = DateTimeOffset.Parse(start, CultureInfo.InvariantCulture);
        DateTimeOffset notAfter = DateTimeOffset.Parse(end, CultureInfo.InvariantCulture);
        EnrollmentIssuer issuer = new(Guid.NewGuid(), new(new ClusterIdentity(Guid.NewGuid(), 1), 1, 1),
            Guid.NewGuid(), Guid.NewGuid(), "public-cert", new string('A', 64), new string('B', 64),
            notBefore, notBefore.AddYears(3));
        context.Set<EnrollmentIssuer>().Add(issuer);
        // Намеренно обходим доменный guard через EF, чтобы проверить сам constraint хранилища.
        context.Entry(issuer).Property(item => item.NotAfterUtc).CurrentValue = notAfter;

        // Act
        Exception? failure = await Record.ExceptionAsync(() => context.SaveChangesAsync(token));

        // Assert
        if (allowed)
        {
            Assert.Null(failure);
            EnrollmentIssuer stored = Assert.Single(await context.Set<EnrollmentIssuer>().AsNoTracking().ToListAsync(token));
            Assert.Equal(notAfter, stored.NotAfterUtc);
        }
        else
        {
            DbUpdateException update = Assert.IsType<DbUpdateException>(failure);
            SqliteException constraint = Assert.IsType<SqliteException>(update.InnerException);
            Assert.Equal(275, constraint.SqliteExtendedErrorCode);
            Assert.Contains("ck_enrollment_issuers_lifetime", constraint.Message, StringComparison.Ordinal);
            Assert.Empty(await context.Set<EnrollmentIssuer>().AsNoTracking().ToListAsync(token));
        }
    }

    [Theory]
    [InlineData(false, 0, false)]
    [InlineData(false, 1, true)]
    [InlineData(false, 65536, true)]
    [InlineData(false, 65537, false)]
    [InlineData(true, 0, false)]
    [InlineData(true, 1, true)]
    [InlineData(true, 65536, true)]
    [InlineData(true, 65537, false)]
    public async Task SQLitePreservesLocalIssuerAndIssuanceDerByteLimits(bool issuance, int length, bool allowed)
    {
        // Arrange
        CancellationToken token = TestContext.Current.CancellationToken;
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync(token);
        await using UptimeHarborDbContext context = new(SqliteTestDatabase.CreateOptions(connection));
        await context.Database.EnsureCreatedAsync(token);
        LocalEnrollmentIssuerRecord issuer = new()
        {
            Id = Guid.NewGuid(), InstallIdentity = Guid.NewGuid(), ClusterId = Guid.NewGuid(), ClusterGeneration = 1,
            NodeId = Guid.NewGuid(), StorageGeneration = Guid.NewGuid(), KeyReference = "local-test-key",
            CertificateDer = [1], CertificateDigest = new string('A', 64), SpkiDigest = new string('B', 64)
        };
        context.Set<LocalEnrollmentIssuerRecord>().Add(issuer);
        string constraintName = "ck_local_enrollment_issuers_der";
        if (issuance)
        {
            await context.SaveChangesAsync(token);
            context.Set<LocalEnrollmentIssuanceRecord>().Add(new()
            {
                Id = Guid.NewGuid(), IssuerEpoch = issuer.Id, AttemptId = Guid.NewGuid(), AuthorizationIndex = 1,
                RequestDigest = new string('C', 64), TemplateDigest = new string('D', 64),
                CertificateDer = new byte[length], CertificateDigest = new string('E', 64)
            });
            constraintName = "ck_local_enrollment_issuances_der";
        }
        else
        {
            issuer.CertificateDer = new byte[length];
        }

        // Act
        Exception? failure = await Record.ExceptionAsync(() => context.SaveChangesAsync(token));

        // Assert
        if (allowed)
        {
            Assert.Null(failure);
            if (issuance)
            {
                LocalEnrollmentIssuanceRecord stored = Assert.Single(
                    await context.Set<LocalEnrollmentIssuanceRecord>().AsNoTracking().ToListAsync(token));
                Assert.Equal(length, stored.CertificateDer.Length);
            }
            else
            {
                LocalEnrollmentIssuerRecord stored = Assert.Single(
                    await context.Set<LocalEnrollmentIssuerRecord>().AsNoTracking().ToListAsync(token));
                Assert.Equal(length, stored.CertificateDer.Length);
            }
        }
        else
        {
            DbUpdateException update = Assert.IsType<DbUpdateException>(failure);
            SqliteException constraint = Assert.IsType<SqliteException>(update.InnerException);
            Assert.Equal(275, constraint.SqliteExtendedErrorCode);
            Assert.Contains(constraintName, constraint.Message, StringComparison.Ordinal);
        }
    }
}
