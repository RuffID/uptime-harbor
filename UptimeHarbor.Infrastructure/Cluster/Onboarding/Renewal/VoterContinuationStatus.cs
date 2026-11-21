using UptimeHarbor.Application.Cluster.Models;

namespace UptimeHarbor.Infrastructure.Cluster.Onboarding.Renewal;

/// <summary>Решения текущего запроса без команд, учётных записей или содержимого журнала.</summary>
public sealed record VoterContinuationStatus(CommandResult Request, string? AuthorizationDigest, CommandResult? Registration);
