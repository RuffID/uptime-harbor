using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Domain.Cluster;

namespace UptimeHarbor.Application.Cluster;

/// <summary>Один whitelist для accepted, Pending и конфликтов; raw payload никогда не покидает Application.</summary>
public static class AdministrationMapping
{
    public static AdministrationEntityDto Entity(AcceptedValue value)
    {
        return new(value.Id, value.Kind.ToString(), value.Revision, value.Deleted, Settings(value.Kind, value.Value));
    }

    public static AdministrationSettingsDto Settings(DeletedEntityKind kind, ConfigurationValue value)
    {
        if (kind == DeletedEntityKind.Account)
        {
            return new() { Name = value.Name, Enabled = value.Enabled };
        }

        return new()
        {
            Name = value.Name, Endpoint = value.Endpoint, Description = value.Description,
            Enabled = value.Enabled, ParentId = value.ParentId, ChatId = value.ChatId,
            TelegramChatId = value.TelegramChatId, ObserverNodes = value.ObserverNodes,
            FailureThreshold = value.FailureThreshold, IntervalSeconds = value.IntervalSeconds,
            TimeoutSeconds = value.TimeoutSeconds
        };
    }

    public static AdministrationOperationDto Operation(SubmissionReceipt receipt, RevisionImage image)
    {
        CommandResult? result = receipt.Result;
        string status = "LocalPending";
        string? message = null;
        if (result is not null)
        {
            status = result.Outcome.ToString();
            if (result.Reason == "AlreadyDecided")
            {
                message = "Исходная заявка уже решена. Отмена или замена не выполнена. Новую правку подайте от актуальной ревизии.";
            }
            else if (result.Outcome == OperationOutcome.Rejected)
            {
                message = "Заявка отклонена: проверьте активность аккаунта, актуальную ревизию и зависимости.";
            }
            else if (result.Outcome == OperationOutcome.Conflict)
            {
                message = "Сохранён конфликт. Первая принятая версия продолжает действовать.";
            }
        }

        AdministrationOperationDto dto = new(receipt.OperationId, status, result?.AppliedRevision,
            result?.ConflictId, result?.ReplacedBy, message);
        if (result?.ReplacedBy is Guid replacement && image.Results.TryGetValue(replacement, out CommandResult? outcome))
        {
            dto = dto with { ReplacementResult = Operation(new(replacement, false, outcome), image) };
        }

        return dto;
    }
}
