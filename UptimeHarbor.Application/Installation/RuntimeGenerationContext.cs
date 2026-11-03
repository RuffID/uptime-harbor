namespace UptimeHarbor.Application.Installation;

/// <summary>Контекст переносится в дочерние callbacks; после drain их публикация отвергается.</summary>
public class RuntimeGenerationContext : IDisposable
{
    private static readonly AsyncLocal<Action?> CHECK = new();
    private readonly Action? previous = CHECK.Value;

    public RuntimeGenerationContext(Action check)
    {
        CHECK.Value = check;
    }

    public static void Verify()
    {
        CHECK.Value?.Invoke();
    }

    public void Dispose()
    {
        CHECK.Value = previous;
    }
}
