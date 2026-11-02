# Persistence repositories

- ObservationCaptureRepository хранит sequence/capture через прежние EFCoreLibrary writers; retry читает без tracking, stream/result pagination ограничена 100. SQL-проекция monitoring пополняется только существующим ProjectionRepository, без отдельного журнала.

## Ответственность

Репозитории адаптируют Application-контракты к базовым операциям `EFCoreLibrary` и материализуют запросы внутри Infrastructure.

Внедрять `IContextGetItemByPredicateRepository<TEntity, UptimeHarborDbContext>`, `IContextCreateItemRepository<TEntity, UptimeHarborDbContext>` и `IContextDeleteItemRepository<TEntity, UptimeHarborDbContext>`. Второй параметр — ключ scoped adapter; не использовать прежние двухпараметрические `IGetItemByPredicateRepository`/`ICreateItemRepository`/`IDeleteItemRepository`.

## Правила

- Не возвращать `IQueryable` за пределы каталога.
- Списковые Application-контракты возвращают `IReadOnlyList<T>`; конкретный materialized `List<T>` остаётся внутренней деталью реализации.
- Выбирать tracking по назначению операции: изменение — tracked, чтение — no-tracking, если не требуется последующее сохранение.
- Не принимать бизнес-решения; получать от use case предметные параметры запроса.
- Для сущностей без безопасного `IEntity<TId>` искать по идентификатору через predicate repository.
- При изменении запросов или контрактов обновлять этот файл и `Documentation/Technical documentation/Persistence.md`.
- `GetEnabledAsync` фильтрует включённые server/container цели в БД и materialize-ит их no-tracking для снимка цикла.
- Оба monitoring repositories работают с единым MonitoredServer и monitored_servers; фильтр Type обязателен в каждом чтении, включая поиск по ID и родителю. ProjectionRepository сохраняет оба типа без смены Type и явно удаляет только bindings удаляемой цели.
- Привязки для подготовки batch-доставки читать no-tracking, а конкретную привязку для фиксации успеха загружать tracked по ID.
- Чтение пользователей и роли для SignalR выполняет отдельный recipient read query, а не per-user методы репозитория. Репозитория ресурсных прав нет.
- `ProjectionRepository` получает уже решённый пакет принятого префикса и только подготавливает изменения одного tracker. Аккаунт/роль не сохраняются через отдельный auto-commit Identity store; сохранение принадлежит UoW вместе с cursor.
- Terminal decisions, controls, tombstones и неизменяемые события нельзя переписывать. Удаление сервера отвязывает контейнеры; удаление цели/чата отменяет неначатые intents. Исторические payload не удаляются каскадом.
- Проверять tombstone и у собственного ID узла/observer assignment/node chat route, а не только у их target/node/chat зависимостей: старая копия не должна восстанавливать удалённое назначение.
- Конфигурационные updates проекции сохраняют monitoring/delivery-поля; identity binding не перемещается. Pending pagination/priority/retry фильтруются до materialization с явным лимитом.
