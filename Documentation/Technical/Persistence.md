# Persistence и транзакции

## Текущее состояние

Persistence реализован в `UptimeHarbor.Infrastructure`. `UptimeHarbor.Application` содержит только контекстно-независимые интерфейсы.

`UptimeHarborDbContext` наследует Identity DbContext с ключом `Guid` и одновременно реализует контекст хранения ключей Data Protection. В модель входят объекты мониторинга, permissions, Telegram-чаты, notification bindings с per-binding состоянием доставки текущего инцидента и durable notification outbox.

## EF-конфигурации

Конфигурации находятся в `Persistence/Configurations` и применяются через `ApplyConfigurationsFromAssembly`.

Они задают:

- отдельные таблицы серверов и контейнеров;
- строковое хранение статуса и check constraints допустимых значений;
- обязательные ключи и `ValueGeneratedNever` для идентификаторов приложения;
- длины строк, индексы и уникальность связей;
- каскадное удаление контейнеров, permissions и notification bindings;
- составные ключи, запрещающие доступ к контейнеру вне назначенного сервера;
- правило notification binding «ровно один target»;
- хранение времён как UTC;
- явный `Version` серверов и контейнеров как concurrency token;
- таблицу `notification_outbox_jobs` с payload, attempt, next-attempt, lease и terminal timestamps без внешнего ключа к удаляемому объекту.

Для идемпотентности уведомлений объект хранит nullable `CurrentIncidentId`, а каждая привязка — nullable идентификаторы инцидента успешной outage- и recovery-доставки. Глобальный `OutageNotificationSent` удалён из кодовой модели, потому что не описывает частичный успех нескольких чатов.

`UtcDateTimeOffsetConverter` преобразует `DateTimeOffset` в UTC `DateTime` провайдера и восстанавливает нулевое смещение при чтении.

## EFCoreLibrary

Локальная `libs/EFCoreLibrary.dll` подключена только к Infrastructure. Через `AddEfCoreBaseRepositories<UptimeHarborDbContext>()` регистрируются базовые create/delete/predicate/query repositories.

Предметные репозитории адаптируют их к Application-контрактам. Для поиска по ID используется predicate repository, поэтому Domain не реализует библиотечный `IEntity<TId>`.

Все внешние списковые read-методы Application repositories возвращают `Task<IReadOnlyList<T>>`. Infrastructure по-прежнему materialize-ит запросы в конкретные `List<T>` через базовые list operations или `ToListAsync`; изменяемый тип не выходит через контракт. Запросы чтения выполняются no-tracking, запрос объекта для последующего изменения остаётся tracked. `IQueryable` не выходит из Infrastructure.

`DashboardSnapshotReadQuery` отдельно проецирует server/container dashboard records без endpoint URL и permission IDs текущего пользователя. `MonitoringRecipientReadQuery` отдельно materialize-ит users с ролью Administrator и пакетные permission ID обычных пользователей: один server-permission query и, только для container event, один container-permission query. Оба контракта находятся в Application, реализации — в Infrastructure; EF entities, provider-типы и `IQueryable` наружу не выходят.

Для начала каждого monitoring cycle `GetEnabledAsync` фильтрует включённые серверы и контейнеры в БД и возвращает materialized no-tracking snapshot. Результат каждой параллельной проверки записывается в отдельном DI scope: объект повторно загружается tracked и сохраняется через scoped `IMonitoringUnitOfWork`.

`Version` увеличивается доменными методами. EF добавляет исходное значение в `UPDATE`/`DELETE`; нулевое число затронутых строк даёт `DbUpdateConcurrencyException`. Технический Unit of Work очищает stale change tracker и переводит исключение в независимый `PersistenceConcurrencyException`. Monitoring считает его устаревшим результатом без событий, а административный use case возвращает semantic conflict.

## Сценарные Unit of Work

Application определяет общий `IUnitOfWorkScope` только с `SaveChangesAsync` и выполнением действия в транзакции.

Сценарные интерфейсы:

- `IMonitoringUnitOfWork`;
- `IObjectAdministrationUnitOfWork`;
- `IVisibilitySettingsUnitOfWork`;
- `INotificationSettingsUnitOfWork`.

Infrastructure использует одну scoped-сессию `IUptimeHarborDbContextSession`. `UptimeHarborDbUnitOfWork` реализует сохранение и транзакцию, а сценарные классы добавляют только нужные репозитории.

`IObjectAdministrationUnitOfWork` раскрывает только серверы и контейнеры. Обычное сохранение атомарно фиксирует изменение настроек, включая сброс состояния при выключении мониторинга. Удаление сервера выполняется транзакционно после повторной проверки подтверждённого количества контейнеров; контейнеры, permissions и notification bindings удаляются существующими каскадными внешними ключами.

Транзакционный вызов открывает транзакцию, выполняет действие, один раз сохраняет изменения и делает commit. При исключении выполняется rollback, после чего исходное исключение передаётся вызывающему коду; отдельная ошибка rollback сохраняется в `Exception.Data` и не заменяет первопричину.

Identity store использует тот же scoped `UptimeHarborDbContext`, что позволяет удалению пользователя и его permissions выполняться внутри сценарной транзакции видимости без глобального Unit of Work.

`IMonitoringUnitOfWork` атомарно сохраняет состояние объекта и outage/recovery outbox job. Отдельный dispatcher claim-ит due jobs коротким lease. Подготовка списка Telegram-доставок выполняет no-tracking чтение привязок. После успешного transport-вызова конкретная привязка повторно загружается tracked и сохраняется через отдельный scope `INotificationSettingsUnitOfWork`, поэтому состояние одного получателя не объединяется с результатом другого.

## Миграции

Миграции не применяются автоматически при старте. Порядок создания и применения описан в [Database migrations.md](Database%20migrations.md).

Для design-time scaffolding используется `UptimeHarborDesignTimeDbContextFactory`. Она настраивает провайдер Npgsql без строки подключения: построение модели и генерация миграции не требуют секрета и не открывают соединение с PostgreSQL.

Первая миграция `20260825121410_InitialPostgreSqlSchema` создана в `Persistence/Migrations`. Она включает доменные таблицы, ASP.NET Core Identity, составные ограничения видимости и таблицу ключей Data Protection.

Миграция `20260825154441_AddNotificationDeliveryState` удаляет глобальный `OutageNotificationSent` и добавляет nullable incident-ID поля объектам и привязкам. Она создана штатным EF tooling и проверена сборкой и model metadata tests.

Миграция `20260830090000_AddMonitoringConcurrencyAndNotificationOutbox` добавляет только `Version` в две monitoring-таблицы и `notification_outbox_jobs`. Она включена в исходный код, но не применялась: применение к PostgreSQL остаётся отдельным ручным release-шагом.
