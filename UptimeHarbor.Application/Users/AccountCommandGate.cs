namespace UptimeHarbor.Application.Users;

/// <summary>Сериализует подготовку salted credentials и durable retry одного узла между scopes.</summary>
public class AccountCommandGate
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
