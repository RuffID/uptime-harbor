namespace UptimeHarbor.Application.Cluster.Models;

public record SubmissionReceipt(Guid OperationId, bool LocalPending, CommandResult? Result);
