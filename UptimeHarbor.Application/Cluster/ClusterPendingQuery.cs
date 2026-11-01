using UptimeHarbor.Application.Abstractions.UnitOfWork;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Common;
using UptimeHarbor.Domain.Cluster;

namespace UptimeHarbor.Application.Cluster;

public class ClusterPendingQuery(ClusterAdministrationAccess access, RevisionStateStore state,
    IPendingControlUnitOfWork work, IRevisionPayloadProtection protection)
{
    public async Task<ServiceResult<IReadOnlyList<AdministrationPendingDto>>> ReadAsync(int offset, CancellationToken token)
    {
        ServiceError? error = access.GetError();
        if (error is not null)
        {
            return ServiceResult<IReadOnlyList<AdministrationPendingDto>>.Fail(error);
        }

        if (offset < 0)
        {
            return ServiceResult<IReadOnlyList<AdministrationPendingDto>>.Fail(ServiceErrorType.Validation, "Некорректная страница.");
        }

        IReadOnlyList<PendingOperation> page = await work.PendingOperations.GetPageAsync(offset, 100, false, token);
        error = access.GetError();
        if (error is not null)
        {
            return ServiceResult<IReadOnlyList<AdministrationPendingDto>>.Fail(error);
        }

        RevisionImage image = state.Current!;
        List<AdministrationPendingDto> rows = [];
        foreach (PendingOperation operation in page)
        {
            RevisionCommand command = RevisionCodec.Decode(protection.Unprotect(operation.ProtectedPayload));
            AdministrationOperationDto? result = null;
            string localState = operation.State.ToString();
            // Результат принятой истории имеет приоритет над устаревшей записью local Pending/backup.
            if (image.Results.TryGetValue(operation.Id, out CommandResult? outcome))
            {
                result = AdministrationMapping.Operation(new(operation.Id, false, outcome), image);
                localState = "Resolved";
            }

            AdministrationSettingsDto? settings = null;
            RevisionCommand displayed = command.Replacement ?? command;
            if (displayed.Value is not null)
            {
                settings = AdministrationMapping.Settings(displayed.EntityKind, displayed.Value);
            }

            rows.Add(new(operation.Id, operation.EntityId, command.EntityKind.ToString(), command.Action,
                displayed.BaseRevision, localState, operation.CreatedAtUtc, settings, operation.RequestedControlId,
                command.Replacement?.OperationId ?? operation.ReplacementOperationId, result, displayed.AccountAction));
        }

        return ServiceResult<IReadOnlyList<AdministrationPendingDto>>.Ok(rows);
    }

    public ServiceResult<AdministrationOperationDto> Find(Guid id)
    {
        ServiceError? error = access.GetError();
        if (error is not null)
        {
            return ServiceResult<AdministrationOperationDto>.Fail(error);
        }

        RevisionImage image = state.Current!;
        if (!image.Results.TryGetValue(id, out CommandResult? result))
        {
            return ServiceResult<AdministrationOperationDto>.Fail(ServiceErrorType.NotFound,
                "Принятый результат пока неизвестен. Это не подтверждает отказ или отмену.");
        }

        return ServiceResult<AdministrationOperationDto>.Ok(AdministrationMapping.Operation(new(id, false, result), image));
    }
}
