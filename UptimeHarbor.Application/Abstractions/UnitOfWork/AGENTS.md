# Scenario unit of work contracts

IOnboardingProgressUnitOfWork ограничен локальным шагом попытки: сохранить intent до сети, удалить protected code после accepted reserve. Это подготовленный порт; реализация setup ещё отсутствует. Общие приглашения сохраняет прежний IProjectionApplyUnitOfWork вместе с decision/receipt/cursor.

## Ответственность

Каталог задаёт технический `IUnitOfWorkScope` и небольшие сценарные Unit of Work для связанных групп операций.

## Правила

- Не создавать единый глобальный Unit of Work со всеми репозиториями.
- Сценарный интерфейс наследует `IUnitOfWorkScope` и раскрывает только нужные ему репозитории.
- Сохранение и транзакции инициировать через сценарный Unit of Work.
- Все репозитории одного сценария должны использовать одну scoped-сессию Infrastructure.
- Администрирование серверов и контейнеров использует отдельный `IObjectAdministrationUnitOfWork`; смена/снятие связи сохраняется в одной транзакции. Контрактов ресурсных permissions нет.
- RemoveNotificationBindingsAsync удаляет bindings только переданного target ID в той же транзакции; при удалении Server не передавать туда ID отвязываемых приложений.
- `IObjectAdministrationUnitOfWork` атомарно отменяет незавершённые recovery jobs прежнего incident при disable/endpoint change, не раскрывая EF/outbox entity в Application.
- Запись результата одной проверки и её notification outbox job использует `IMonitoringUnitOfWork` в одном `SaveChangesAsync`; каждый параллельно проверяемый объект получает отдельный DI scope и собственную scoped-сессию.
- EF concurrency exception переводится Infrastructure в независимый `PersistenceConcurrencyException`; Application не зависит от EF Core.
- Известные unique conflicts, включая административное имя сервера, переводятся Infrastructure в `PersistenceConflictException` с семантическим `PersistenceConflictKind`; неизвестные persistence errors не маскируются.
- Администрирование и фиксация успешной доставки одной привязки используют сценарный `INotificationSettingsUnitOfWork`; получатели сохраняются независимо.
- При изменении границ сценариев обновлять реализации, DI, этот файл и `Documentation/Technical documentation/Persistence.md`.
- `IProjectionApplyUnitOfWork` атомарно сохраняет готовый принятый пакет, аккаунт/роль/версию, зависимости, receipt и cursor; повтор проверяет позицию и digest. Он не доказывает Raft commit и не выполняет reducer.
- Pending controls, локальные capture sequence + observation и результаты доставки имеют отдельные UoW. Локальное подтверждение записи не означает принятие общим журналом.
