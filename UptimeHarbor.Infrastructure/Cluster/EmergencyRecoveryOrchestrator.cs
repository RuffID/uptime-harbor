using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Security;
using UptimeHarbor.Infrastructure.Cluster.Models;
using UptimeHarbor.Infrastructure.Identity;
using UptimeHarbor.Infrastructure.Persistence;

namespace UptimeHarbor.Infrastructure.Cluster;

/// <summary>Офлайн готовит новую карантинную копию после ручной изоляции. Не запускает host, SQL или peers.</summary>
public class EmergencyRecoveryOrchestrator(IPasswordHasher<ApplicationUser> hasher, TimeProvider time)
{
    public const string LOSS_ACKNOWLEDGEMENT = "I accept the loss of changes absent from the selected committed copy.";

    public async Task<PublishedRevisionSnapshot> PrepareAsync(EmergencyRecoverySelection selection, ClusterOptions target,
        OperationalNode node, OperationalCredential credential, string administratorPassword, CancellationToken token)
    {
        await RecordDecisionAsync(selection, "Selected", token);
        try
        {
            return await PrepareCoreAsync(selection, target, node, credential, administratorPassword, token);
        }
        catch
        {
            await RecordDecisionAsync(selection, "DeclinedOrRejected", CancellationToken.None);
            throw;
        }
    }

    public async Task RecordDecisionAsync(EmergencyRecoverySelection selection, string outcome, CancellationToken token)
    {
        if (!Path.IsPathFullyQualified(selection.SnapshotPath) || !File.Exists(selection.SnapshotPath)
            || outcome is not ("Selected" or "DeclinedOrRejected"))
        {
            throw new InvalidDataException("RecoveryAuditSourceInvalid");
        }

        byte[] entry = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new
        {
            selection.RecoveryId, selection.AdministratorId, selection.Isolation.OperatorId,
            selection.PreviousClusterId, selection.SourceIndex, selection.SourceTerm,
            SourceDigest = selection.SnapshotDigest, Outcome = outcome, AtUtc = time.GetUtcNow()
        });
        await using FileStream output = new(selection.SnapshotPath + ".recovery-audit.jsonl", FileMode.Append,
            FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough);
        await output.WriteAsync(entry, token);
        await output.WriteAsync("\n"u8.ToArray(), token);
        await output.FlushAsync(token);
        output.Flush(true);
    }

    private async Task<PublishedRevisionSnapshot> PrepareCoreAsync(EmergencyRecoverySelection selection, ClusterOptions target,
        OperationalNode node, OperationalCredential credential, string administratorPassword, CancellationToken token)
    {
        if (selection.LossAcknowledgement != LOSS_ACKNOWLEDGEMENT || selection.RecoveryId == Guid.Empty
            || selection.AdministratorId == Guid.Empty || selection.PreviousClusterId == target.ClusterId
            || !Path.IsPathFullyQualified(selection.SnapshotPath) || target.Mode != "Bootstrap")
        {
            throw new InvalidDataException("RecoveryConfirmationRequired");
        }

        if (Directory.Exists(target.StoragePath) && Directory.EnumerateFileSystemEntries(target.StoragePath).Any())
        {
            throw new InvalidDataException("RecoveryTargetMustBeEmpty");
        }

        ClusterOptions previous = new()
        {
            ClusterId = selection.PreviousClusterId, Generation = selection.PreviousGeneration,
            ProtocolMajor = target.ProtocolMajor, ProtocolMinor = target.ProtocolMinor,
            CommandSchema = target.CommandSchema, SnapshotFormat = target.SnapshotFormat,
            SqlSchema = target.SqlSchema, PasswordHashFormat = target.PasswordHashFormat
        };
        RevisionSnapshotStore reader = new(Options.Create(previous));
        PublishedRevisionSnapshot selected = await reader.ReadAsync(selection.SnapshotPath, selection.SnapshotDigest, token);
        RevisionImage source = selected.Image;
        if (source.Index != selection.SourceIndex || source.Term != selection.SourceTerm
            || selection.KnownCommittedIndex < source.Index || source.Operations.Nodes.IsEmpty
            || source.Operations.BotId != target.ExpectedBotId)
        {
            throw new InvalidDataException("RecoverySelectedCutMismatch");
        }

        using UptimeHarborDbContext context = new(new DbContextOptionsBuilder<UptimeHarborDbContext>().UseSqlServer().Options);
        SnapshotImageValidation.Validate(context, source);
        if (!AcceptedAdministratorAccessPolicy.IsActiveAdministrator(RevisionAccounts.Find(source, selection.AdministratorId)))
        {
            throw new UnauthorizedAccessException("RecoveryAdministratorNotActiveInSelectedCopy");
        }

        ConfigurationValue account = source.Values[selection.AdministratorId].Value;
        if (account.PasswordHashFormat != 1 || hasher.VerifyHashedPassword(new(selection.AdministratorId, account.Name!),
            account.PasswordHash!, administratorPassword) == PasswordVerificationResult.Failed)
        {
            throw new UnauthorizedAccessException("RecoveryAdministratorReauthenticationFailed");
        }

        Dictionary<Guid, Guid> former = source.Operations.Nodes.Values.ToDictionary(item => item.NodeId, item => item.StorageGeneration);
        string evidence = ManualIsolationValidation.Validate(selection.Isolation, former, time.GetUtcNow(), target.ExpectedBotId, true);
        RevisionImage baseline = RecoveryBaselineFactory.Create(source, target, selection, node, credential, evidence, time.GetUtcNow());
        Directory.CreateDirectory(target.StoragePath);
        await WriteAuditAsync(target, selection, "SelectedAndConfirmed", null, token);
        try
        {
            RevisionSnapshotStore writer = new(Options.Create(target));
            PublishedRevisionSnapshot snapshot = await writer.PublishAsync(baseline, token);
            await WriteAuditAsync(target, selection, "Quarantined", snapshot.Digest, token);
            return snapshot;
        }
        catch
        {
            await WriteAuditAsync(target, selection, "PreparationFailed", null, CancellationToken.None);
            throw;
        }
    }

    private static async Task WriteAuditAsync(ClusterOptions target, EmergencyRecoverySelection selection,
        string outcome, string? resultDigest, CancellationToken token)
    {
        string file = Path.Combine(target.StoragePath, "recovery-audit.jsonl");
        byte[] entry = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new
        {
            selection.RecoveryId, selection.AdministratorId, selection.Isolation.OperatorId,
            selection.PreviousClusterId, selection.PreviousGeneration, selection.SourceIndex, selection.SourceTerm,
            SourceDigest = selection.SnapshotDigest, selection.KnownCommittedIndex,
            target.ClusterId, target.Generation, Outcome = outcome, ResultDigest = resultDigest
        });
        await using FileStream stream = new(file, FileMode.Append, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough);
        await stream.WriteAsync(entry, token);
        await stream.WriteAsync("\n"u8.ToArray(), token);
        await stream.FlushAsync(token);
        stream.Flush(true);
    }
}
