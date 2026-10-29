namespace UptimeHarbor.Infrastructure.Identity;

/// <summary>Сериализует локальные counters между scopes одного экземпляра NodeId.</summary>
public class LocalAccountLoginGate
{
    private readonly SemaphoreSlim serial = new(1, 1);

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
