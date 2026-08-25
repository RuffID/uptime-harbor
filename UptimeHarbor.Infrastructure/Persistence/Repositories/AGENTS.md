# Persistence repositories

## Ответственность

Репозитории адаптируют Application-контракты к базовым операциям `EFCoreLibrary` и материализуют запросы внутри Infrastructure.

## Правила

- Не возвращать `IQueryable` за пределы каталога.
- Выбирать tracking по назначению операции: изменение — tracked, чтение — no-tracking, если не требуется последующее сохранение.
- Не принимать бизнес-решения; получать от use case предметные параметры запроса.
- Для сущностей без безопасного `IEntity<TId>` искать по идентификатору через predicate repository.
- При изменении запросов или контрактов обновлять этот файл и `Documentation/Technical/Persistence.md`.
- `GetEnabledAsync` фильтрует включённые server/container цели в БД и materialize-ит их no-tracking для снимка цикла.
