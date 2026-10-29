namespace UptimeHarbor.Application.Security;

public record AcceptedAccount(Guid UserId, long Revision, bool IsEnabled, bool IsAdministrator, bool IsDeleted);
