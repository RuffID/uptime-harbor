using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Domain.Cluster;

namespace UptimeHarbor.Infrastructure.Cluster;

/// <summary>Получателей восстанавливает только сохранённая accepted попытка, не Planned intent.</summary>
public static class MonitoringSnapshotDelivery
{
    public static void Validate(RevisionImage image, Guid intentId)
    {
        MonitoringIntent intent = image.Monitoring.Intents[intentId];
        if (!image.Monitoring.ConfirmedDeliveries.TryGetValue(intentId, out MonitoringDeliveryEvidence? proof)
            || proof.IntentId != intentId || proof.AcceptedIndex > image.Index || proof.AcceptedTerm > image.Term
            || intent.CompletedAtUtc != proof.SucceededAtUtc)
        {
            throw new InvalidDataException("DeliveryEvidenceMissing");
        }

        SharedSnapshotRow row = image.ProjectionRows.Single(row => row.Entity == typeof(DeliveryIntent).FullName
            && row.Fields["Id"].GetGuid() == intentId);
        if (row.Fields["State"].GetInt32() != (int)DeliveryState.Succeeded
            || row.Fields["AttemptCount"].GetInt32() < proof.AttemptNumber
            || row.Fields["CompletedAtUtc"].GetDateTimeOffset() != proof.SucceededAtUtc
            || row.Fields["NextAttemptAtUtc"].ValueKind != System.Text.Json.JsonValueKind.Null)
        {
            throw new InvalidDataException("DeliveryProjectionMismatch");
        }

        // Включены настоящие записи всех попыток, а не синтетические BeginAttempt для счётчика.
        SharedSnapshotRow[] attempts = image.ProjectionRows.Where(row => row.Entity == typeof(DeliveryAttempt).FullName
            && row.Fields["IntentId"].GetGuid() == intentId).OrderBy(row => row.Fields["Number"].GetInt32()).ToArray();
        int count = row.Fields["AttemptCount"].GetInt32();
        if (attempts.Length != count || !attempts.Select(row => row.Fields["Number"].GetInt32()).SequenceEqual(Enumerable.Range(1, count)))
        {
            throw new InvalidDataException("DeliveryAttemptHistoryMissing");
        }
    }
}
