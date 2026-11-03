# EF configurations

EnrollmentIssuerConfiguration задаёт public projection enrollment_issuers; две LocalEnrollment* configurations не являются ProjectionRecord. Issuance Restrict FK относится к local issuer; operational credential не создаётся до существующего Node. Purpose/validity/authorization checks и unique key/SPKI/pins поддерживать вместе с моделью и metadata tests. Genesis journal index=0 требует GenesisId; обычная выдача требует accepted index>0.

LocalSetupAccount/LocalProfileRecord/LocalAttemptRecord/LocalStepRecord configurations задают четыре локальные таблицы setup/profile/attempt/step и revision concurrency tokens. Их не добавлять в whitelist общей проекции/snapshot; миграции генерировать tooling только с отдельным разрешением.

OnboardingProjection хранится в onboarding_invitations с version/status constraints и concurrency по LogIndex, в общей projection transaction. Payload содержит verifier, никогда plaintext. Migration/designer/snapshot получать tooling; provider atomicity не выводить из metadata model tests.

## Ответственность

Каталог задаёт таблицы, ключи, ограничения, индексы, связи и правила удаления для доменной и Identity-модели.

## Правила

- Каждую сущность настраивать отдельным `IEntityTypeConfiguration<T>`.
- Дублировать критичные инварианты ограничениями БД, когда они должны действовать при любом способе записи.
- Не менять имена таблиц, ключей и constraints без оценки миграции и совместимости.
- SQL в check constraints и filtered indexes писать в синтаксисе Microsoft SQL Server.
- При изменении конфигураций обновлять model tests, этот файл и `Documentation/Technical documentation/Persistence.md`.
- Состояние частичной доставки хранить на `NotificationBinding` по incident ID; не возвращать глобальный флаг объекту мониторинга.
- Unique indexes Telegram chat ID и обеих chat/target пар имеют стабильные явные имена, используемые при сопровождении SQL Server conflict classification.
- Оба типа MonitoredServer хранятся в monitored_servers с кодами Type Server / Application. Nullable self FK ServerId и оба target FK уведомлений используют NoAction. Уникальность NormalizedName действует только для Type=Server. Прежние permission configurations исключаются фильтром assembly scan.
- MonitoredServer.Version настраивать как optimistic concurrency token для обоих типов.
- `MonitoredServer.Name` настраивать как обязательное поле с уникальным индексом `ux_monitored_servers_name`.
- `MonitoredServer.StartedAtUtc` хранить nullable с общей UTC-конвертацией: до первой успешной проверки время загрузки неизвестно.
- Outbox job не связывать внешним ключом с удаляемым объектом мониторинга: сохранённый payload должен переживать административное удаление.
- `ProjectionRecord` не является mapped inheritance root; общие provenance-поля настраивает helper `ProjectionConfiguration`. Каждая конкретная запись имеет свою таблицу.
- Нормализованный login имеет уникальный индекс и BIN2 collation в SQL Server; роль ограничена Administrator. Локальные lockout и секреты не смешивать с общими credential-полями.
- Уникальные позиции receipts, observer stream/sequence, terminal controls и delivery logical keys защищают идемпотентность. Исторические связи incidents/events/attempts/summaries используют Restrict; компактные tombstones не очищаются по возрасту.
