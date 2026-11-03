namespace UptimeHarbor.Infrastructure.Installation;

/// <summary>Generation lease охватывает всю операцию до commit/transport; drain ждёт её освобождения.</summary>
public class RuntimeWriteGate
{
    private readonly object sync = new();
    private long generation;
    private bool open;
    private int active;
    private TaskCompletionSource drained = Completed();

    public void Verify(long expectedGeneration)
    {
        lock (sync)
        {
            if (!open || generation != expectedGeneration)
            {
                throw new InvalidOperationException("StaleRuntimeGeneration");
            }
        }
    }

    public void Open(long nextGeneration)
    {
        lock (sync)
        {
            if (active != 0 || nextGeneration <= generation)
            {
                throw new InvalidOperationException("RuntimeGenerationConflict");
            }

            generation = nextGeneration;
            open = true;
        }
    }

    public IDisposable Enter(long expectedGeneration)
    {
        lock (sync)
        {
            if (!open || expectedGeneration != generation)
            {
                throw new InvalidOperationException("StaleRuntimeGeneration");
            }

            if (active++ == 0)
            {
                drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            return new Lease(this);
        }
    }

    public Task CloseAsync()
    {
        lock (sync)
        {
            open = false;
            return drained.Task;
        }
    }

    private static TaskCompletionSource Completed()
    {
        TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        completion.SetResult();
        return completion;
    }

    private class Lease(RuntimeWriteGate owner) : IDisposable
    {
        private int disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0)
            {
                return;
            }

            lock (owner.sync)
            {
                if (--owner.active == 0)
                {
                    owner.drained.TrySetResult();
                }
            }
        }
    }
}
