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
- Все административные use case получают единый семантический отказ через `IAdministratorAccessPolicy`; controller authorization не заменяет эту проверку.
- Администрирование объектов выполнять через `IObjectAdministrationUnitOfWork`, а уведомление после сохранения — через `IObjectAdministrationChangePublisher`.
- Административное имя сервера проверять на уникальность без учёта регистра; полученное endpoint внутреннее имя не использовать вместо него.
- Disable/endpoint change через `IObjectAdministrationUnitOfWork` атомарно отменяет recovery jobs прежнего incident; Application не знает EF/outbox entity.
- Мониторинг координировать через `MonitoringService`: endpoint проверяет независимый контракт, состояние изменяется доменными переходами, событие публикуется только после сохранения.
- `MonitoringOptions` принадлежит Application как общий типизированный контракт цикла и HTTP-адаптера; привязка конфигурации остаётся в Web composition root.
- Главная панель загружается через `DashboardQuery` и специализированный materialized `IDashboardSnapshotReadQuery`; endpoint URL не входит в read records, заданное и внутреннее имена сервера передаются раздельно, а фильтрация выполняется до формирования dashboard DTO.
- Dashboard и SignalR-получатели сохраняют чистую `ObjectVisibilityPolicy` для совместимости; администратору доступны все цели. `IMonitoringRecipientReadQuery` загружает пользователей/роль одним запросом и возвращает пустые коллекции прежних permissions. Ресурсных назначений в целевой модели нет; публичного `/api/objects` в v1 нет.
- Storage-контракты общей модели находятся в `Abstractions/Persistence` и `Abstractions/UnitOfWork`: готовая проекция принятого префикса отделена от локальных Pending/capture/results. Согласование, reducer и общий вход этими контрактами не реализуются.
- `IObjectAdministrationChangePublisher` вызывается только после успешного сохранения; Web-реализация обязана изолировать best-effort SignalR transport error.
- Уведомления используют независимый `INotificationSender`, административный use case и per-binding фиксацию outage/recovery через `INotificationSettingsUnitOfWork`; transport и логирование остаются в Infrastructure.
- `UserManagementService` запрещает текущему администратору отключать или удалять собственную учётную запись.
- `UserManagementService` также запрещает отключение и удаление последнего включённого администратора; ожидаемый отказ возвращается как `ServiceErrorType.Conflict`.
- Telegram delivery использует durable outbox с at-least-once transport-семантикой; Application не заявляет exactly-once для внешнего Telegram API.
- Известный persistence race уникальности Telegram chat/binding возвращается как `ServiceResult.Conflict`; provider exception остаётся в Infrastructure.
