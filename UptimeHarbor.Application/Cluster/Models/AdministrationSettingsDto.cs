namespace UptimeHarbor.Application.Cluster.Models;

/// <summary>Явный браузерный whitelist. Защищённые поля внутреннего ConfigurationValue сюда не входят.</summary>
public record AdministrationSettingsDto
{
    public string? Name { get; init; }
    public string? Endpoint { get; init; }
    public string? Description { get; init; }
    public bool Enabled { get; init; } = true;
    public Guid? ParentId { get; init; }
    public Guid? ChatId { get; init; }
    public long TelegramChatId { get; init; }
    public IReadOnlyList<Guid> ObserverNodes { get; init; } = [];
    public int FailureThreshold { get; init; } = 3;
    public int IntervalSeconds { get; init; } = 60;
    public int TimeoutSeconds { get; init; } = 10;
}
