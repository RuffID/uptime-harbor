# Persistence repositories

## Ответственность

Репозитории адаптируют Application-контракты к базовым операциям `EFCoreLibrary` и материализуют запросы внутри Infrastructure.

## Правила

- Не возвращать `IQueryable` за пределы каталога.
- Списковые Application-контракты возвращают `IReadOnlyList<T>`; конкретный materialized `List<T>` остаётся внутренней деталью реализации.
- Выбирать tracking по назначению операции: изменение — tracked, чтение — no-tracking, если не требуется последующее сохранение.
- Не принимать бизнес-решения; получать от use case предметные параметры запроса.
- Для сущностей без безопасного `IEntity<TId>` искать по идентификатору через predicate repository.
- При изменении запросов или контрактов обновлять этот файл и `Documentation/Technical/Persistence.md`.
- `GetEnabledAsync` фильтрует включённые server/container цели в БД и materialize-ит их no-tracking для снимка цикла.
- Привязки для подготовки batch-доставки читать no-tracking, а конкретную привязку для фиксации успеха загружать tracked по ID.
- Пакетное чтение пользователей, ролей и permissions для SignalR выполняет отдельный recipient read query, а не per-user методы репозитория.
