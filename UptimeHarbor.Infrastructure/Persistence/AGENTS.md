# Infrastructure persistence

## Ответственность

Каталог содержит `UptimeHarborDbContext`, EF-конфигурации, реализации репозиториев, scoped-сессию контекста и сценарные Unit of Work.

## Правила

- Все репозитории и Unit of Work в одном DI scope используют один `UptimeHarborDbContext`.
- Модель EF настраивать под защищённую доменную модель, не ослабляя её публичный API.
- Сохранять ограничения, индексы, каскады и UTC-конвертацию согласованными с доменными правилами. Связь server → containers использует `NoAction`, чтобы SQL Server не создавал multiple cascade paths; сценарий удаления сервера явно удаляет загруженные контейнеры в той же транзакции.
- Существующие миграции вручную не редактировать; новые создаются отдельной командой после изменения модели.
- Pending migrations автоматически применяются единственным Web-процессом при старте только после успешного SQL Server backup и до Identity bootstrap; multi-instance migration coordination не реализована.
- Design-time scaffolding использует `UptimeHarborDesignTimeDbContextFactory` без строки подключения и не открывает соединение с БД.
- EF-конфигурации и миграции должны быть совместимы с Microsoft SQL Server; PostgreSQL-специфичные типы и SQL не использовать.
- При изменении схемы или транзакционных границ обновлять этот файл, `Documentation/Technical documentation/Persistence.md` и документ миграций.
- Цикл загружает включённые цели no-tracking, а результат каждого объекта повторно загружается tracked и сохраняется через отдельный scoped `IMonitoringUnitOfWork`.
- Специализированные read queries для dashboard и SignalR recipients проецируют только Application read records и полностью materialize-ят их внутри Infrastructure; endpoint URL не входит в dashboard projection.
- Per-binding outage/recovery marker сохраняется отдельным scoped `INotificationSettingsUnitOfWork` после успешной transport-доставки; схема входит в SQL Server baseline `20260831015604_InitialSqlServerSchema`.
- Серверы и контейнеры используют явный `Version` как concurrency token; `DbUpdateConcurrencyException` очищает tracking и переводится в независимый Application-конфликт.
- Административное имя сервера обязательно и защищено уникальным индексом `ux_monitored_servers_name`; известный SQL Server unique conflict переводится в `DuplicateServerName`.
- Outage/recovery outbox job сохраняется атомарно с результатом monitoring; dispatcher хранит attempt, lease, retry и terminal state в `notification_outbox_jobs`.
- Disable/endpoint change атомарно записывает terminal cancellation recovery job в существующий JSON payload/`CompletedAtUtc`; dispatcher сериализует pre-send gate с этой отменой.
- Известные SQL Server unique violations имени сервера и Telegram chat/binding классифицируются по 2601/2627 и EF entity; неизвестные `DbUpdateException` не считаются конфликтом.
