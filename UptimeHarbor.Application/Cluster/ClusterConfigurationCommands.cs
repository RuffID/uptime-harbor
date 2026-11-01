using UptimeHarbor.Application.Abstractions.UnitOfWork;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Common;
using UptimeHarbor.Domain.Cluster;

namespace UptimeHarbor.Application.Cluster;

public class ClusterConfigurationCommands(ClusterAdministrationAccess access, RevisionStateStore state,
    PendingCommandService pending, IPendingControlUnitOfWork work, IRevisionPayloadProtection protection,
    TimeProvider time)
{
    public async Task<ServiceResult<AdministrationOperationDto>> SubmitAsync(ConfigurationChangeRequest request,
        Guid nodeId, CancellationToken token)
    {
        ServiceError? error = access.GetError();
        if (error is not null)
        {
            return ServiceResult<AdministrationOperationDto>.Fail(error);
        }

        try
        {
            Guid submissionId = request.ControlOperationId ?? request.OperationId;
            PendingOperation? prior = await work.PendingOperations.GetAsync(submissionId, token);
            RevisionCommand command;
            if (prior is not null)
            {
                command = RevisionCodec.Decode(protection.Unprotect(prior.ProtectedPayload));
                RevisionCommand candidate = command.Replacement ?? command;
                if (!AdministrationCommandFactory.Matches(request, candidate)
                    || command.Target?.OperationId != request.TargetOperationId)
                {
                    return Failure(ServiceErrorType.Conflict, "Идентификатор операции уже используется. Ввод сохранён; запросите исходный результат.");
                }
            }
            else
            {
                RevisionImage image = state.Current!;
                if (image.Results.ContainsKey(submissionId))
                {
                    return Failure(ServiceErrorType.Conflict, "Операция уже решена. Запросите исходный результат по её идентификатору.");
                }

                command = AdministrationCommandFactory.Prepare(request, image, time.GetUtcNow());
                if (request.TargetOperationId is Guid targetId)
                {
                    RevisionCommand target = await LoadTargetAsync(targetId, token);
                    if (target.EntityId != command.EntityId || target.EntityKind != command.EntityKind)
                    {
                        return Failure(ServiceErrorType.Validation, "Замена должна относиться к той же сущности.");
                    }

                    command = command with { OperationId = request.ControlOperationId!.Value, Action = "Replace",
                        Value = null, ResolvesConflictId = null, Target = target, Replacement = command };
                }
            }

            SubmissionReceipt receipt;
            if (request.ConfirmRestored)
            {
                receipt = await pending.ConfirmRestoredAsync(request.TargetOperationId!.Value, command, nodeId, token);
            }
            else
            {
                receipt = await pending.SubmitAsync(command, nodeId, token);
            }

            return ServiceResult<AdministrationOperationDto>.Ok(AdministrationMapping.Operation(receipt, state.Current!));
        }
        catch (UnauthorizedAccessException)
        {
            return Failure(ServiceErrorType.Forbidden, "Требуется активный принятый администратор.");
        }
        catch (ArgumentException)
        {
            return Failure(ServiceErrorType.Validation, "Проверьте поля, observers, зависимости и подтверждение глобального удаления.");
        }
        catch (InvalidOperationException exception) when (IsExpected(exception.Message))
        {
            return Failure(ServiceErrorType.Conflict, "Заявка не изменена. Проверьте результат, текущую ревизию и подтверждение после восстановления.");
        }
    }

    public async Task<ServiceResult<AdministrationOperationDto>> CancelAsync(Guid targetId, Guid operationId,
        Guid nodeId, CancellationToken token)
    {
        ServiceError? error = access.GetError();
        if (error is not null)
        {
            return ServiceResult<AdministrationOperationDto>.Fail(error);
        }

        if (operationId == Guid.Empty || targetId == operationId)
        {
            return Failure(ServiceErrorType.Validation, "Некорректный идентификатор отмены.");
        }

        try
        {
            PendingOperation? prior = await work.PendingOperations.GetAsync(operationId, token);
            RevisionCommand target = await LoadTargetAsync(targetId, token);
            RevisionCommand command = target with { OperationId = operationId, Action = "Cancel", Value = null,
                Target = target, Replacement = null, ResolvesConflictId = null, CreatedAtUtc = time.GetUtcNow() };
            if (prior is not null)
            {
                command = RevisionCodec.Decode(protection.Unprotect(prior.ProtectedPayload));
                if (command.Action != "Cancel" || command.Target?.OperationId != targetId)
                {
                    return Failure(ServiceErrorType.Conflict, "Идентификатор операции уже используется.");
                }
            }

            SubmissionReceipt receipt = await pending.SubmitAsync(command, nodeId, token);
            return ServiceResult<AdministrationOperationDto>.Ok(AdministrationMapping.Operation(receipt, state.Current!));
        }
        catch (UnauthorizedAccessException)
        {
            return Failure(ServiceErrorType.Forbidden, "Требуется активный принятый администратор.");
        }
        catch (InvalidOperationException exception) when (IsExpected(exception.Message))
        {
            return Failure(ServiceErrorType.Conflict, "Отмена не подтверждена. Перечитайте исходный результат заявки.");
        }
    }

    private async Task<RevisionCommand> LoadTargetAsync(Guid id, CancellationToken token)
    {
        PendingOperation target = await work.PendingOperations.GetAsync(id, token)
            ?? throw new InvalidOperationException("PendingTargetMissing");
        RevisionCommand command = RevisionCodec.Decode(protection.Unprotect(target.ProtectedPayload));
        if (command.Action is not ("Upsert" or "Delete"))
        {
            throw new InvalidOperationException("UnsupportedPendingControl");
        }

        return command;
    }

    private static bool IsExpected(string reason)
    {
        return reason is "PendingQueueFull" or "IdempotencyKeyReuse" or "PendingTargetMissing"
            or "PendingRequiresReconciliation" or "StaleConfirmation" or "ExplicitReplacementRequired"
            or "UnsupportedPendingControl";
    }

    private static ServiceResult<AdministrationOperationDto> Failure(ServiceErrorType type, string message)
    {
        return ServiceResult<AdministrationOperationDto>.Fail(type, message);
    }
}
