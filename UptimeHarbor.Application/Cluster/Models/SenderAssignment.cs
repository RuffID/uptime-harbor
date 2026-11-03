namespace UptimeHarbor.Application.Cluster.Models;

public record SenderAssignment(int Version, Guid ClusterId, long Generation, long BotId,
    Guid SenderNodeId, Guid StorageGeneration, long AssignmentRevision, Guid SenderEpoch);
