using Microsoft.Extensions.Hosting;
using UptimeHarbor.Application.Installation;

namespace UptimeHarbor.Infrastructure.Installation;

/// <summary>Управляемый loop: повторный старт запрещён до полного завершения прошлого поколения.</summary>
public abstract class RestartableWorker : BackgroundService
{
    private readonly object sync = new();
    private CancellationTokenSource? cancellation;
    private Task completion = Task.CompletedTask;
    private RuntimeWriteGate? gate;
    private long generation;
    public Task Completion => completion;

    public void ConfigureGeneration(RuntimeWriteGate writeGate, long runtimeGeneration)
    {
        if (cancellation is not null)
        {
            throw new InvalidOperationException("WorkerAlreadyStarted");
        }

        gate = writeGate;
        generation = runtimeGeneration;
    }

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        lock (sync)
        {
            if (!completion.IsCompleted || cancellation is not null)
            {
                throw new InvalidOperationException("WorkerAlreadyStarted");
            }

            cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            CancellationToken workerToken = cancellation.Token;
            long capturedGeneration = generation;
            RuntimeWriteGate? capturedGate = gate;
            completion = Task.Run(async () =>
            {
                if (capturedGate is null)
                {
                    await ExecuteAsync(workerToken);
                    return;
                }

                using IDisposable operation = capturedGate.Enter(capturedGeneration);
                using RuntimeGenerationContext context = new(() => capturedGate.Verify(capturedGeneration));
                await ExecuteAsync(workerToken);
            }, CancellationToken.None);
            return Task.CompletedTask;
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        Task pending;
        lock (sync)
        {
            cancellation?.Cancel();
            pending = completion;
        }

        try
        {
            await pending.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (pending.IsCanceled)
        {
            // Подтверждённая отмена loop считается завершённым drain.
        }

        lock (sync)
        {
            cancellation?.Dispose();
            cancellation = null;
        }
    }

    public override void Dispose()
    {
        cancellation?.Cancel();
        base.Dispose();
    }
}
