using UptimeHarbor.Application.Abstractions.UnitOfWork;
using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Common;
using UptimeHarbor.Application.Security;
using UptimeHarbor.Application.Users.Models;
using UptimeHarbor.Domain.Cluster;

namespace UptimeHarbor.Application.Users;

/// <summary>Подготавливает credentials и подаёт аккаунтные команды в единственный общий pipeline.</summary>
public class SharedUserManagementService(IUserAccessContext user, IAdministratorAccessPolicy policy,
    RevisionStateStore state, PendingCommandService pending, IPendingControlUnitOfWork work,
    IRevisionPayloadProtection protection, IAccountCredentialService credentials, TimeProvider time,
    AccountCommandGate commands)
{
    public ServiceResult<IReadOnlyList<UserAccountDto>> GetUsers()
    {
        ServiceError? error = AccessError();
        if (error is not null)
        {
            return ServiceResult<IReadOnlyList<UserAccountDto>>.Fail(error);
        }

        UserAccountDto[] accounts = state.Current!.Values.Values
            .Where(value => value.Kind == DeletedEntityKind.Account && !value.Deleted)
            .OrderBy(value => value.Value.Name)
            .Select(value => new UserAccountDto(value.Id, value.Value.Name!, value.Value.Enabled, true, [], [])
            {
                Revision = value.Revision
            }).ToArray();
        return ServiceResult<IReadOnlyList<UserAccountDto>>.Ok(accounts);
    }

    public async Task<ServiceResult<AccountOperationDto>> SubmitAsync(AccountChangeRequest request,
        Guid nodeId, CancellationToken token)
    {
        try
        {
            return await commands.ExecuteAsync(() => SubmitCoreAsync(request, nodeId, token), token);
        }
        catch (UnauthorizedAccessException)
        {
            return ServiceResult<AccountOperationDto>.Fail(ServiceErrorType.Forbidden, "Требуется активный принятый администратор.");
        }
        catch (InvalidOperationException exception) when (exception.Message is "PendingQueueFull" or "IdempotencyKeyReuse")
        {
            return ServiceResult<AccountOperationDto>.Fail(ServiceErrorType.Conflict, "Заявка не сохранена: очередь заполнена или OperationId уже используется.");
        }
    }

    private async Task<ServiceResult<AccountOperationDto>> SubmitCoreAsync(AccountChangeRequest request,
        Guid nodeId, CancellationToken token)
    {
        ServiceError? error = AccessError();
        if (error is not null)
        {
            return ServiceResult<AccountOperationDto>.Fail(error);
        }

        if (request.OperationId == Guid.Empty || request.UserId == Guid.Empty || nodeId == Guid.Empty
            || request.BaseRevision < 0 || request.Action is not ("Create" or "Update" or "Password" or "SetEnabled" or "Delete"))
        {
            return InvalidRequest();
        }

        bool changesLogin = request.Action is "Create" or "Update";
        bool changesPassword = request.Action is "Create" or "Password";
        if (!changesLogin && request.Login is not null || !changesPassword && request.Password is not null
            || request.Action != "SetEnabled" && request.IsEnabled.HasValue)
        {
            return InvalidRequest();
        }

        // Retry читает уже подготовленный защищённый вариант: hash salt и timestamp не генерируются заново.
        PendingOperation? existing = await work.PendingOperations.GetAsync(request.OperationId, token);
        if (existing is not null)
        {
            RevisionCommand original = RevisionCodec.Decode(protection.Unprotect(existing.ProtectedPayload));
            if (!Matches(request, original) || original.ActorUserId != user.UserId || original.OriginNodeId != nodeId)
            {
                return ServiceResult<AccountOperationDto>.Fail(ServiceErrorType.Conflict, "Идентификатор операции уже используется.");
            }

            if (existing.State == PendingState.NeedsUserConfirmation)
            {
                return ServiceResult<AccountOperationDto>.Fail(ServiceErrorType.Conflict, "Заявка требует подтверждения после восстановления.");
            }

            SubmissionReceipt retry = await pending.SubmitAsync(original, nodeId, token);
            return Result(request.UserId, retry);
        }

        RevisionImage image = state.Current!;
        if (image.Results.ContainsKey(request.OperationId))
        {
            // Результат чужой/утраченной локальной заявки читается отдельно; не создаём новый payload с прежним ID.
            return ServiceResult<AccountOperationDto>.Fail(ServiceErrorType.Conflict, "Операция уже решена. Запросите её результат.");
        }

        AcceptedValue? current = image.Values.GetValueOrDefault(request.UserId);
        if (request.Action == "Create")
        {
            if (request.BaseRevision != 0 || current is not null || string.IsNullOrWhiteSpace(request.Password))
            {
                return InvalidRequest();
            }
        }
        else if (current is null || current.Kind != DeletedEntityKind.Account || current.Deleted)
        {
            return ServiceResult<AccountOperationDto>.Fail(ServiceErrorType.NotFound, "Пользователь не найден.");
        }

        if (current is not null && current.Revision != request.BaseRevision)
        {
            return ServiceResult<AccountOperationDto>.Fail(ServiceErrorType.Conflict, "Версия пользователя изменилась.");
        }

        ConfigurationValue value = current?.Value ?? new ConfigurationValue();
        if (request.Action is "Create" or "Update")
        {
            if (string.IsNullOrWhiteSpace(request.Login))
            {
                return InvalidRequest();
            }

            string normalized;
            try
            {
                normalized = SharedAccount.NormalizeLogin(request.Login);
            }
            catch (ArgumentException)
            {
                return InvalidRequest();
            }

            if (image.Values.Values.Any(account => account.Kind == DeletedEntityKind.Account
                && account.Id != request.UserId && SharedAccount.NormalizeLogin(account.Value.Name!) == normalized))
            {
                return ServiceResult<AccountOperationDto>.Fail(ServiceErrorType.Conflict, "Логин уже занят.");
            }

            value = value with { Name = request.Login.Normalize(System.Text.NormalizationForm.FormKC).Trim() };
            if (request.Action == "Update")
            {
                value = value with { SecurityStamp = Guid.NewGuid().ToString("N"), ConcurrencyStamp = Guid.NewGuid().ToString("N") };
            }
        }

        if (request.Action is "Create" or "Password")
        {
            if (string.IsNullOrWhiteSpace(request.Password))
            {
                return InvalidRequest();
            }

            ServiceResult<ProtectedCredential> prepared = await credentials.PrepareAsync(
                request.UserId, value.Name!, request.Password, token);
            if (!prepared.Success)
            {
                return ServiceResult<AccountOperationDto>.Fail(prepared.Error!);
            }

            ProtectedCredential credential = prepared.Data!;
            value = value with { PasswordHash = credential.PasswordHash, PasswordHashFormat = credential.FormatVersion,
                SecurityStamp = credential.SecurityStamp, ConcurrencyStamp = credential.ConcurrencyStamp };
        }
        else if (request.Password is not null)
        {
            return InvalidRequest();
        }

        if (request.Action == "Create")
        {
            value = value with { RoleId = image.Values.Values.First(account => account.Kind == DeletedEntityKind.Account).Value.RoleId };
        }
        else if (request.Action == "SetEnabled")
        {
            if (!request.IsEnabled.HasValue)
            {
                return InvalidRequest();
            }

            value = value with { Enabled = request.IsEnabled.Value, SecurityStamp = Guid.NewGuid().ToString("N"),
                ConcurrencyStamp = Guid.NewGuid().ToString("N") };
        }

        bool deactivating = request.Action == "Delete" || !value.Enabled;
        if (current is not null && deactivating)
        {
            int active = image.Values.Values.Count(account => account.Kind == DeletedEntityKind.Account
                && !account.Deleted && account.Value.Enabled);
            try
            {
                RevisionAccounts.Materialize(current).EnsureCanDeactivate(user.UserId, active);
            }
            catch (InvalidOperationException)
            {
                return ServiceResult<AccountOperationDto>.Fail(ServiceErrorType.Conflict, "Нельзя отключить или удалить эту учётную запись.");
            }
        }

        string action = "Upsert";
        ConfigurationValue? payload = value;
        if (request.Action == "Delete")
        {
            action = "Delete";
            payload = null;
        }

        RevisionCommand command = new()
        {
            ClusterId = image.ClusterId, ClusterGeneration = image.Generation, OperationId = request.OperationId,
            OriginNodeId = nodeId, ActorUserId = user.UserId, ActorRevision = state.Find(user.UserId)!.Revision,
            EntityId = request.UserId, EntityKind = DeletedEntityKind.Account, BaseRevision = request.BaseRevision,
            Action = action, AccountAction = request.Action, Value = payload, CreatedAtUtc = time.GetUtcNow()
        };
        SubmissionReceipt receipt = await pending.SubmitAsync(command, nodeId, token);
        return Result(request.UserId, receipt);
    }

    public ServiceResult<AccountOperationDto> Find(Guid operationId)
    {
        ServiceError? error = AccessError();
        if (error is not null)
        {
            return ServiceResult<AccountOperationDto>.Fail(error);
        }

        CommandResult? result = state.Current!.Results.GetValueOrDefault(operationId);
        if (result is null || !result.SubjectUserId.HasValue)
        {
            return ServiceResult<AccountOperationDto>.Fail(ServiceErrorType.NotFound, "Принятый результат пока отсутствует.");
        }

        return Result(result.SubjectUserId.Value, new SubmissionReceipt(operationId, false, result));
    }

    private bool Matches(AccountChangeRequest request, RevisionCommand original)
    {
        if (original.EntityKind != DeletedEntityKind.Account || original.AccountAction != request.Action || original.EntityId != request.UserId
            || original.BaseRevision != request.BaseRevision)
        {
            return false;
        }

        if (request.Action == "Delete")
        {
            return original.Action == "Delete" && request.Password is null;
        }

        ConfigurationValue? value = original.Value;
        if (value is null || original.Action != "Upsert")
        {
            return false;
        }

        if (request.Action is "Create" or "Update")
        {
            if (string.IsNullOrWhiteSpace(request.Login)
                || value.Name != request.Login.Normalize(System.Text.NormalizationForm.FormKC).Trim())
            {
                return false;
            }
        }

        if (request.Action is "Create" or "Password")
        {
            if (string.IsNullOrWhiteSpace(request.Password))
            {
                return false;
            }

            ProtectedCredential credential = new(value.PasswordHash!, value.PasswordHashFormat,
                value.SecurityStamp!, value.ConcurrencyStamp!);
            return credentials.Verify(request.UserId, value.Name!, credential, request.Password);
        }

        if (request.Password is not null)
        {
            return false;
        }

        return request.Action != "SetEnabled" || request.IsEnabled == value.Enabled;
    }

    private ServiceError? AccessError()
    {
        ServiceError? error = policy.GetAccessError();
        if (error is not null)
        {
            return error;
        }

        if (!AcceptedAdministratorAccessPolicy.IsActiveAdministrator(state.Find(user.UserId)))
        {
            return new ServiceError(ServiceErrorType.Forbidden, "Требуется активный принятый администратор.");
        }

        return null;
    }

    private static ServiceResult<AccountOperationDto> InvalidRequest()
    {
        return ServiceResult<AccountOperationDto>.Fail(ServiceErrorType.Validation, "Некорректная команда пользователя.");
    }

    private static ServiceResult<AccountOperationDto> Result(Guid userId, SubmissionReceipt receipt)
    {
        string status = "Pending";
        if (receipt.Result is not null)
        {
            status = receipt.Result.Outcome.ToString();
        }

        return ServiceResult<AccountOperationDto>.Ok(new AccountOperationDto(receipt.OperationId, userId,
            status, receipt.Result?.AppliedRevision, receipt.Result?.ConflictId));
    }
}
