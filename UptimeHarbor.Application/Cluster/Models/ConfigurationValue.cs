using System.Collections.Immutable;

namespace UptimeHarbor.Application.Cluster.Models;

/// <summary>Внутреннее представление общего значения. Не является пользовательским DTO.</summary>
public record ConfigurationValue
{
    public string? Name { get; init; }
    public string? Endpoint { get; init; }
    public string? Description { get; init; }
    public bool Enabled { get; init; } = true;
    public Guid? ParentId { get; init; }
    public Guid? ChatId { get; init; }
    public long TelegramChatId { get; init; }
    public Guid Generation { get; init; }
    public Guid RoleId { get; init; }
    public string? PasswordHash { get; init; }
    public string? SecurityStamp { get; init; }
    public string? ConcurrencyStamp { get; init; }
    public int PasswordHashFormat { get; init; } = 1;
    public ImmutableArray<Guid> ObserverNodes { get; init; } = [];
    public int FailureThreshold { get; init; } = 3;
    public int IntervalSeconds { get; init; } = 60;
    public int TimeoutSeconds { get; init; } = 10;
    public DateTimeOffset AssignedAtUtc { get; init; } = DateTimeOffset.UnixEpoch;

    public override string ToString()
    {
        return "[protected configuration]";
    }
}
