using UptimeHarbor.Application.Common;

namespace UptimeHarbor.Application.Installation;

/// <summary>Только публичный BotId нового первого; не sender assignment или доставка.</summary>
public interface ISetupBotIdentity
{
    Task<ServiceResult<long>> ReadAsync(CancellationToken token);
}
