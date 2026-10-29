using UptimeHarbor.Application.Common;

namespace UptimeHarbor.Application.Notifications;

public interface IAcceptedTestDelivery
{
    Task<ServiceResult> SubmitAsync(Guid chatId, CancellationToken token);
}
