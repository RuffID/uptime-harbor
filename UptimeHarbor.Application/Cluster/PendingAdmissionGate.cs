namespace UptimeHarbor.Application.Cluster;

/// <summary>Ограничивает локальную durable очередь и сериализует проверку ёмкости между scopes.</summary>
public class PendingAdmissionGate
{
    private readonly SemaphoreSlim serial = new(1, 1);
    private readonly int maximumPending;
    private readonly int controlReserve;

    public PendingAdmissionGate(int maximumPending = 10000, int controlReserve = 1000)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumPending);
        if (controlReserve < 0 || controlReserve >= maximumPending)
        {
            throw new ArgumentOutOfRangeException(nameof(controlReserve));
        }

        this.maximumPending = maximumPending;
        this.controlReserve = controlReserve;
    }

    public void EnsureCapacity(long pending, bool control)
    {
        int limit = maximumPending - controlReserve;
        if (control)
        {
            limit = maximumPending;
        }

        if (pending >= limit)
        {
            throw new InvalidOperationException("PendingQueueFull");
        }
    }

    public async Task<T> ExecuteAsync<T>(Func<Task<T>> action, CancellationToken token)
    {
        await serial.WaitAsync(token);
        try
        {
            return await action();
        }
        finally
        {
            serial.Release();
        }
    }
}
