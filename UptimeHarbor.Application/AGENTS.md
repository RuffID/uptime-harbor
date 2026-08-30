# UptimeHarbor.Application

## Ответственность

Проект содержит use case, DTO и контекстно-независимые контракты репозиториев и сценарных Unit of Work. Единственная project reference — `UptimeHarbor.Domain`.

## Правила

- Не использовать `DbContext`, `IQueryable`, `TContext`, tracking, `Include` и другие EF-детали.
- Use case зависит от минимального сценарного Unit of Work и возвращает DTO либо принятый result-контракт.
- Проверки, требующие I/O, текущего пользователя или нескольких агрегатов, координировать здесь.
- Не помещать реализации интеграций и Web-типы в Application.
- При изменении контрактов или сценариев обновлять этот файл и соответствующую техническую документацию.
- Ожидаемые ошибки сценариев возвращать через `ServiceResult`; HTTP-статус выбирает только Web.
- Для ошибок конкретного поля использовать необязательный `ServiceError.Field`; формат HTTP-ответа по-прежнему определяет Web.
- Проверки текущего пользователя получать через `IUserAccessContext`, не добавляя ASP.NET Core dependency.
- Администрирование объектов выполнять через `IObjectAdministrationUnitOfWork`, а уведомление после сохранения — через `IObjectAdministrationChangePublisher`.
- Мониторинг координировать через `MonitoringService`: endpoint проверяет независимый контракт, состояние изменяется доменными переходами, событие публикуется только после сохранения.
- `MonitoringOptions` принадлежит Application как общий типизированный контракт цикла и HTTP-адаптера; привязка конфигурации остаётся в Web composition root.
- Главная панель загружается через независимый `DashboardQuery`, который фильтрует сущности до формирования отдельных dashboard DTO и рассчитывает uptime через `TimeProvider`.
- HTTP-чтение и SignalR-получатели используют общую `ObjectVisibilityPolicy`; доступ к серверу не заменяет отдельное разрешение контейнера.
- Уведомления используют независимый `INotificationSender`, административный use case и per-binding фиксацию outage/recovery через `INotificationSettingsUnitOfWork`; transport и логирование остаются в Infrastructure.
- `UserManagementService` запрещает текущему администратору отключать или удалять собственную учётную запись.
- `UserManagementService` также запрещает отключение и удаление последнего включённого администратора; ожидаемый отказ возвращается как `ServiceErrorType.Conflict`.
- Telegram delivery использует durable outbox с at-least-once transport-семантикой; Application не заявляет exactly-once для внешнего Telegram API.
