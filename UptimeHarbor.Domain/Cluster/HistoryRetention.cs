namespace UptimeHarbor.Domain.Cluster;

public static class HistoryRetention
{
    public static bool CanRemoveDetails(DateTimeOffset? completedAtUtc, DateTimeOffset nowUtc,
        HistoryProtection protection)
    {
        StorageGuards.Utc(nowUtc);
        if (!completedAtUtc.HasValue || protection != HistoryProtection.None)
        {
            return false;
        }

        StorageGuards.Utc(completedAtUtc.Value);
        // Календарный год, включая високосные границы; завершение определяет владеющий агрегат.
        if (completedAtUtc.Value > nowUtc || completedAtUtc.Value.Year == 9999)
        {
            return false;
        }

        return completedAtUtc.Value.AddYears(1) <= nowUtc;
    }
}
