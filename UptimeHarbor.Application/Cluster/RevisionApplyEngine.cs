using UptimeHarbor.Application.Cluster.Models;

namespace UptimeHarbor.Application.Cluster;

/// <summary>Единственная граница публикации принятого состояния после атомарного сохранения проекции.</summary>
public class RevisionApplyEngine(RevisionStateStore state, RevisionReducer reducer,
    IRevisionProjectionStore projection)
{
    private readonly SemaphoreSlim serial = new(1, 1);

    public async Task ApplyConfigurationAsync(long index, long term, Guid nodeId, CancellationToken token)
    {
        await serial.WaitAsync(token);
        try
        {
            RevisionImage current = state.Current ?? throw new InvalidOperationException("CheckpointUnavailable");
            string digest = RevisionCodec.Digest(System.Text.Encoding.UTF8.GetBytes($"configuration/{index}/{term}"));
            if (index <= current.Index)
            {
                if (index != current.Index || current.Term != term || current.Digest != digest)
                {
                    throw new InvalidDataException("ConfigurationPrefixMismatch");
                }

                await projection.VerifyAsync(current, token);
                return;
            }

            if (term < current.Term)
            {
                throw new InvalidDataException("ConfigurationTermMismatch");
            }

            RevisionImage next = current with { Index = index, Term = term, Digest = digest };
            await projection.ApplyAsync(RevisionWriteSetFactory.Create(current, next, nodeId), token);
            state.Publish(next);
        }
        catch
        {
            state.Invalidate();
            throw;
        }
        finally
        {
            serial.Release();
        }
    }

    public async Task<bool> ConfirmRecoveryAsync(Func<RevisionImage, Task<bool>> verify, CancellationToken token)
    {
        await serial.WaitAsync(token);
        try
        {
            long recoveryVersion = state.RecoveryVersion;
            RevisionImage? image = state.Current;
            if (image is null || !await verify(image))
            {
                state.Invalidate();
                return false;
            }

            return state.ConfirmRecovery(image, recoveryVersion);
        }
        catch
        {
            state.Invalidate();
            throw;
        }
        finally
        {
            serial.Release();
        }
    }

    public async Task<RevisionImage> CaptureAsync(CancellationToken cancellationToken)
    {
        await serial.WaitAsync(cancellationToken);
        try
        {
            if (!state.Available || state.Current is null)
            {
                throw new InvalidOperationException("ProjectionUnavailable");
            }

            return await projection.ExportAsync(state.Current, cancellationToken);
        }
        finally
        {
            serial.Release();
        }
    }

    public async Task ApplyAsync(RevisionCommand command, long index, long term, Guid nodeId,
        CancellationToken cancellationToken)
    {
        await serial.WaitAsync(cancellationToken);
        try
        {
            RevisionImage current = state.Current ?? throw new InvalidOperationException("CheckpointUnavailable");
            if (index <= current.Index)
            {
                if (index != current.Index || current.Digest != RevisionCodec.Digest(RevisionCodec.Encode(command)))
                {
                    throw new InvalidDataException("AppliedPrefixMismatch");
                }

                await projection.VerifyAsync(current, cancellationToken);
                return;
            }

            RevisionImage next = reducer.Apply(current, command, index, term);
            await projection.ApplyAsync(RevisionWriteSetFactory.Create(current, next, nodeId), cancellationToken);
            state.Publish(next);
        }
        catch
        {
            state.Invalidate();
            throw;
        }
        finally
        {
            serial.Release();
        }
    }

    public async Task InstallAsync(RevisionImage image, Guid snapshotId, string snapshotDigest,
        CancellationToken cancellationToken)
    {
        await serial.WaitAsync(cancellationToken);
        try
        {
            RevisionImage? current = state.Current;
            if (current is not null && (current.ClusterId != image.ClusterId
                || current.Generation != image.Generation || image.Index < current.Index))
            {
                throw new InvalidDataException("StaleSnapshot");
            }

            state.Invalidate();
            await projection.InstallAsync(image, snapshotId, snapshotDigest, cancellationToken);
            state.Publish(image with { ProjectionRows = [] });
        }
        catch
        {
            state.Invalidate();
            throw;
        }
        finally
        {
            serial.Release();
        }
    }
}
