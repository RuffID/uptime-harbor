# Scenario unit of work contracts

## Ответственность

Каталог задаёт технический `IUnitOfWorkScope` и небольшие сценарные Unit of Work для связанных групп операций.

## Правила

- Не создавать единый глобальный Unit of Work со всеми репозиториями.
- Сценарный интерфейс наследует `IUnitOfWorkScope` и раскрывает только нужные ему репозитории.
- Сохранение и транзакции инициировать через сценарный Unit of Work.
- Все репозитории одного сценария должны использовать одну scoped-сессию Infrastructure.
- Администрирование серверов и контейнеров использует отдельный `IObjectAdministrationUnitOfWork`.
- Запись результата одной проверки и её notification outbox job использует `IMonitoringUnitOfWork` в одном `SaveChangesAsync`; каждый параллельно проверяемый объект получает отдельный DI scope и собственную scoped-сессию.
- EF concurrency exception переводится Infrastructure в независимый `PersistenceConcurrencyException`; Application не зависит от EF Core.
- Администрирование и фиксация успешной доставки одной привязки используют сценарный `INotificationSettingsUnitOfWork`; получатели сохраняются независимо.
- При изменении границ сценариев обновлять реализации, DI, этот файл и `Documentation/Technical/Persistence.md`.
