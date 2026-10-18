# Persistence unit of work

## Ответственность

Каталог реализует общую scoped-сессию контекста, техническое сохранение/транзакцию и тонкие сценарные Unit of Work.

## Правила

- Не добавлять глобальный Unit of Work.
- Сценарный класс раскрывает только репозитории соответствующего Application-интерфейса.
- `ObjectAdministrationUnitOfWork` раскрывает только репозитории серверов и контейнеров.
- `ObjectAdministrationUnitOfWork` условным update отменяет recovery jobs прежнего incident в той же транзакции, что disable/endpoint change; job не удаляется, причина хранится в payload.
- `MonitoringUnitOfWork` используется для атомарной записи результата одного объекта и outage/recovery outbox job; параллельные проверки не разделяют его DI scope.
- Технический Unit of Work преобразует EF concurrency exception в `PersistenceConcurrencyException` и очищает stale tracking.
- SQL Server 2601/2627 преобразовывать в `PersistenceConflictException` только для известных Telegram entities; прочие `DbUpdateException` пробрасывать.
- `NotificationSettingsUnitOfWork` фиксирует результат одной привязки в отдельном delivery scope, чтобы частичный успех получателей не объединялся.
- Транзакция выполняет действие, единое сохранение и commit; при ошибке выполняется rollback и сохраняется исходное исключение.
- Не создавать новый `DbContext` внутри репозитория или Unit of Work.
- При изменении транзакционной семантики обновлять тесты, этот файл и `Documentation/Technical documentation/Persistence.md`.
