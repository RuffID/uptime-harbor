namespace UptimeHarbor.Application.Users.Models;

public record UserAccountRecord(
    Guid Id,
    string Login,
    bool IsEnabled,
    bool IsAdministrator);
