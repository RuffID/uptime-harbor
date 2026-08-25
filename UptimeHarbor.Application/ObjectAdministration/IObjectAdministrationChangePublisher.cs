namespace UptimeHarbor.Application.ObjectAdministration;

public interface IObjectAdministrationChangePublisher
{
    Task PublishObjectsChangedAsync(CancellationToken cancellationToken = default);
}
