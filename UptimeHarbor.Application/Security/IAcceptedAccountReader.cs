namespace UptimeHarbor.Application.Security;

/// <summary>Чтение только применённого общего аккаунта; локальные claims не являются источником прав.</summary>
public interface IAcceptedAccountReader
{
    AcceptedAccount? Find(Guid userId);
}
