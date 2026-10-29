using Microsoft.Extensions.Options;
using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Common;
using UptimeHarbor.Application.Users;
using UptimeHarbor.Domain.Cluster;
using UptimeHarbor.Infrastructure.Cluster;

namespace UptimeHarbor.Infrastructure.Identity;

/// <summary>Аккаунтная часть controlled bootstrap. Внешний lifecycle обязан сохранить и подтвердить точную команду.</summary>
public class ClusterAccountBootstrapper(IOptions<ClusterOptions> configured, IAccountCredentialService credentials,
    IRecoveryAdmission admission, RevisionStateStore state, IConsensusWriter consensus, IRevisionAudit audit)
{
    public async Task<ServiceResult<RevisionCommand>> PrepareAsync(Guid operationId, Guid userId, Guid roleId,
        DateTimeOffset createdAtUtc, string login, string password, CancellationToken token)
    {
        ClusterOptions options = configured.Value;
        if (options.Mode != "Bootstrap" || state.Current?.Values.Values.Any(value => value.Kind == DeletedEntityKind.Account) == true)
        {
            return ServiceResult<RevisionCommand>.Fail(ServiceErrorType.Conflict, "Первый аккаунт создаётся только в новой сетке.");
        }

        StorageGuards.Id(operationId, nameof(operationId));
        StorageGuards.Id(userId, nameof(userId));
        StorageGuards.Id(roleId, nameof(roleId));
        StorageGuards.Utc(createdAtUtc);
        SharedAccount.NormalizeLogin(login);
        ServiceResult<ProtectedCredential> prepared = await credentials.PrepareAsync(userId, login, password, token);
        if (!prepared.Success)
        {
            return ServiceResult<RevisionCommand>.Fail(prepared.Error!);
        }

        ProtectedCredential credential = prepared.Data!;
        RevisionCommand command = new()
        {
            ClusterId = options.ClusterId, ClusterGeneration = options.Generation, OperationId = operationId,
            OriginNodeId = options.NodeId, ActorUserId = userId, ActorRevision = 1, EntityId = userId,
            EntityKind = DeletedEntityKind.Account, Action = "BootstrapAccount", BaseRevision = 0,
            CreatedAtUtc = createdAtUtc, Value = new ConfigurationValue
            {
                Name = login.Normalize(System.Text.NormalizationForm.FormKC).Trim(), RoleId = roleId,
                PasswordHash = credential.PasswordHash, PasswordHashFormat = credential.FormatVersion,
                SecurityStamp = credential.SecurityStamp, ConcurrencyStamp = credential.ConcurrencyStamp
            }
        };
        RevisionCodec.Validate(command);
        return ServiceResult<RevisionCommand>.Ok(command);
    }

    public async Task<bool> InitializeAsync(CancellationToken token)
    {
        ClusterOptions options = configured.Value;
        if (options.Mode != "Bootstrap" || !state.Available)
        {
            return false;
        }

        RecoveryAdmission? proof = await admission.ReadAsync(token);
        RevisionCommand? command = proof?.BootstrapAccount;
        if (proof is null || command is null || !proof.FencingConfirmed || proof.OldBackup
            || proof.ClusterId != options.ClusterId || proof.Generation != options.Generation
            || proof.NodeId != options.NodeId || proof.StorageGeneration != options.StorageGeneration
            || command.OriginNodeId != options.NodeId || command.Action != "BootstrapAccount")
        {
            return false;
        }

        string digest = RevisionCodec.Digest(command);
        RevisionImage image = state.Current!;
        if (image.BootstrapAccountDigest != digest || image.Results.ContainsKey(command.OperationId)
            || image.Values.Values.Any(value => value.Kind == DeletedEntityKind.Account))
        {
            return false;
        }

        await consensus.ReplicateAsync(RevisionCodec.Encode(command), RevisionCodec.COMMAND_ID, token);
        CommandResult? result = state.Current?.Results.GetValueOrDefault(command.OperationId);
        if (!state.Available || result is null || result.Digest != digest || result.Outcome != OperationOutcome.Committed)
        {
            throw new InvalidOperationException("AccountBootstrapNotApplied");
        }

        audit.RecordAccount(command.OperationId, command.ActorUserId, command.EntityId, command.OriginNodeId, "Committed");
        return true;
    }
}
