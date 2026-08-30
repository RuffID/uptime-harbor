# Application persistence contracts

## Ответственность

Каталог определяет независимые от технологии хранения базовые и предметные контракты репозиториев.

## Правила

- Все списковые чтения возвращают `IReadOnlyList<T>` с materialized данными, но не изменяемый `List<T>` и не `IQueryable`.
- Не принимать выражения EF, `asNoTracking`, `Include` или тип контекста.
- Общие CRUD-операции выносить в базовые интерфейсы; предметный интерфейс дополнять только специализированными запросами.
- Решения о tracking и форме EF-запроса оставлять Infrastructure.
- При изменении контрактов синхронизировать реализации, DI, тесты, этот файл и `Documentation/Technical/Persistence.md`.
- Списковые чтения для Application materialize-ить в Infrastructure; фильтрацию доступа и формирование DTO выполнять в use case.
- Специализированные dashboard/recipient read-query контракты находятся в Application отдельно от write-oriented репозиториев; их EF-реализации и materialization остаются в Infrastructure.
- Для цикла мониторинга репозитории возвращают materialized no-tracking списки только включённых объектов через `GetEnabledAsync`.
- Чтение привязок для подготовки уведомлений выполняется materialized no-tracking; успешный маркер конкретной доставки записывается после отдельной tracked-загрузки по ID.
