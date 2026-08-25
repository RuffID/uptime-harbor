using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace UptimeHarbor.Infrastructure.Persistence;

public class UtcDateTimeOffsetConverter : ValueConverter<DateTimeOffset, DateTime>
{
    public UtcDateTimeOffsetConverter()
        : base(
            value => value.UtcDateTime,
            value => new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc)))
    {
    }
}
