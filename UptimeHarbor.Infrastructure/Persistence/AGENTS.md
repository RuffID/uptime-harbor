# Infrastructure persistence

enrollment_issuers — общая public projection; local_enrollment_issuers/local_enrollment_issuances — локальные SQL references и immutable DER journal, без shared export. EnrollmentSigningUnitOfWork использует прежнюю scoped session; SQL commit выполняется отдельно от majority/files/private-key effect. Миграция AddEnrollmentTrust сгенерирована с отдельного разрешения без БД, вручную её не менять.

local_setup_accounts/local_installation_profiles/local_onboarding_attempts/local_attempt_steps локальны, вне ProjectionRecord/SnapshotSchema. Installation preflight проверяет каждую mapped shared/local queue/Identity-таблицу. SaveChanges (sync/async) проверяет ambient RuntimeGeneration до записи; схема Setup готовится отдельно без startup migration.

## Ответственность

Каталог содержит `UptimeHarborDbContext`, EF-конфигурации, реализации репозиториев, scoped-сессию контекста и сценарные Unit of Work.

## Правила

- Все репозитории и Unit of Work в одном DI scope используют один `UptimeHarborDbContext`.
- Модель EF настраивать под защищённую доменную модель, не ослабляя её публичный API. Compatibility permission-типы исключать до assembly scan, а их прежние конфигурации пропускать фильтром; не добавлять их явно с последующим Ignore.
- Сохранять ограничения, индексы, каскады и UTC-конвертацию согласованными с доменными правилами. Связь server → containers использует `NoAction`, чтобы SQL Server не создавал multiple cascade paths; сценарий удаления сервера явно отвязывает загруженные контейнеры в той же транзакции, сохраняя контейнеры и их Telegram bindings. `ServerId` nullable; alternate key по паре server/container не используется. Ресурсные permissions исключены из целевой модели.
- Существующие миграции вручную не редактировать; новые создаются отдельной командой после изменения модели.
- Последнее время загрузки сервера хранится в nullable `MonitoredServer.StartedAtUtc` с общей UTC-конвертацией; изменение этой модели требует отдельной migration без выдуманного backfill времени запуска.
- Pending migrations автоматически применяются единственным Web-процессом при старте только после успешного SQL Server backup и до Identity bootstrap; multi-instance migration coordination не реализована.
- Design-time scaffolding использует `UptimeHarborDesignTimeDbContextFactory` без строки подключения и не открывает соединение с БД.
- EF-конфигурации и миграции должны быть совместимы с Microsoft SQL Server; PostgreSQL-специфичные типы и SQL не использовать.
- При изменении схемы или транзакционных границ обновлять этот файл, `Documentation/Technical documentation/Persistence.md` и документ миграций.
- Цикл загружает включённые цели no-tracking, а результат каждого объекта повторно загружается tracked и сохраняется через отдельный scoped `IMonitoringUnitOfWork`.
- Специализированные read queries для dashboard и SignalR recipients проецируют только Application read records и полностью materialize-ят их внутри Infrastructure; endpoint URL не входит в dashboard projection.
- Per-binding outage/recovery marker сохраняется отдельным scoped `INotificationSettingsUnitOfWork` после успешной transport-доставки; схема входит в SQL Server baseline `20260831015604_InitialSqlServerSchema`.
- Серверы и контейнеры используют явный `Version` как concurrency token; `DbUpdateConcurrencyException` очищает tracking и переводится в независимый Application-конфликт.
- Единая таблица monitored_servers хранит Server и Application со стабильными кодами Type. Имя обязательно для обоих типов; уникальный индекс ux_monitored_servers_name ограничен Type=Server. Известный SQL Server unique conflict переводится в DuplicateServerName.
- Outage/recovery outbox job сохраняется атомарно с результатом monitoring; dispatcher хранит attempt, lease, retry и terminal state в `notification_outbox_jobs`.
- Disable/endpoint change атомарно записывает terminal cancellation recovery job в существующий JSON payload/`CompletedAtUtc`; dispatcher сериализует pre-send gate с этой отменой.
- Известные SQL Server unique violations имени сервера и Telegram chat/binding классифицируются по 2601/2627 и EF entity; неизвестные `DbUpdateException` не считаются конфликтом.
- Принятые общие данные восстанавливаются из Raft log + snapshot. SQL receipts содержат позицию/digest без payload; SQL не хранит term/vote WAL и не принимает решения о consensus.
- Проекция аккаунта, роли, зависимостей, receipt и cursor сохраняется одним `ProjectionApplyUnitOfWork`; после rollback tracker очищается. История ссылается Restrict FK, tombstones и terminal decisions защищают от повторов старых копий.
- Pending, observation capture и transport results локальны; accepted observers/incidents/intents/summaries и membership/trust metadata общие. Подробная граница задана в `Documentation/Technical documentation/Shared cluster storage.md`.
