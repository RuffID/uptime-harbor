# Application persistence contracts

ILocalOnboardingAttemptRepository описывает локальный durable intent и CAS по revision; реализация относится к setup, отсутствует в этапе приглашений. Не включать локальный код/ключи/OperationIds шага в общий snapshot и не выдавать порт за готовое хранилище.

- Observation capture repository раскрывает Find по ID и bounded pages streams/results (до 100); sequence/capture writes сохраняются существующим сценарным UoW. Принятый frontier служит ACK, но локальный capture не объявляется accepted и не удаляется по ACK.

## Ответственность

Каталог определяет независимые от технологии хранения базовые и предметные контракты репозиториев.

## Правила

- Все списковые чтения возвращают `IReadOnlyList<T>` с materialized данными, но не изменяемый `List<T>` и не `IQueryable`.
- MonitoredServers / MonitoredContainers — совместимые типовые представления единого MonitoredServer; реализации обязательно фильтруют ServerType. ProjectionWriteSet.Servers содержит оба типа, отдельного набора Containers нет.
- Не принимать выражения EF, `asNoTracking`, `Include` или тип контекста.
- Общие CRUD-операции выносить в базовые интерфейсы; предметный интерфейс дополнять только специализированными запросами.
- Решения о tracking и форме EF-запроса оставлять Infrastructure.
- При изменении контрактов синхронизировать реализации, DI, тесты, этот файл и `Documentation/Technical documentation/Persistence.md`.
- Списковые чтения для Application materialize-ить в Infrastructure; фильтрацию доступа и формирование DTO выполнять в use case.
- Специализированные dashboard/recipient read-query контракты находятся в Application отдельно от write-oriented репозиториев; их EF-реализации и materialization остаются в Infrastructure.
- Для цикла мониторинга репозитории возвращают materialized no-tracking списки только включённых объектов через `GetEnabledAsync`.
- Чтение привязок для подготовки уведомлений выполняется materialized no-tracking; успешный маркер конкретной доставки записывается после отдельной tracked-загрузки по ID.
- `ProjectionWriteSet` переносит готовые изменения доказанного принятого префикса, а не команды для согласования. Receipt не содержит replay payload и не заменяет Raft WAL; авторизация и reducer не относятся к репозиторию проекции.
- Локальные Pending, capture наблюдений и результаты транспорта используют отдельные узкие контракты. `IHistoryRetentionQuery` только читает причины защиты истории и не запускает очистку.
- Pending читать ограниченными страницами по устойчивому ID; priority/due/backlog — отдельные запросы. Не материализовывать всю очередь ради выбора пакета. Ресурсных ACL repository-контрактов нет.
