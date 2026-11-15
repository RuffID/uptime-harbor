using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using UptimeHarbor.Domain.Cluster;

namespace UptimeHarbor.Tests.Infrastructure.Persistence;

/// <summary>Меняет только SQL Server-синтаксис lifetime constraint в тестовой SQLite-модели.</summary>
internal class SqliteTestModelCustomizer(ModelCustomizerDependencies dependencies) : RelationalModelCustomizer(dependencies)
{
    public override void Customize(ModelBuilder modelBuilder, DbContext context)
    {
        base.Customize(modelBuilder, context);
        modelBuilder.Entity<EnrollmentIssuer>().ToTable("enrollment_issuers", table =>
            table.HasCheckConstraint("ck_enrollment_issuers_lifetime",
                "enrollment_issuer_lifetime_valid([NotBeforeUtc], [NotAfterUtc])"));
    }
}
