# Persistence и транзакции

## Текущее состояние

Persistence реализован в `UptimeHarbor.Infrastructure` на EF Core с провайдером Microsoft SQL Server. `UptimeHarbor.Application` содержит только контекстно-независимые интерфейсы.

`UptimeHarborDbContext` наследует Identity DbContext с ключом `Guid` и одновременно реализует контекст хранения ключей Data Protection. В модель входят объекты мониторинга, permissions, Telegram-чаты, notification bindings с per-binding состоянием доставки текущего инцидента и durable notification outbox.

## EF-конфигурации

Конфигурации находятся в `Persistence/Configurations` и применяются через `ApplyConfigurationsFromAssembly`.

Они задают:

- отдельные таблицы серверов и контейнеров;
- строковое хранение статуса и check constraints допустимых значений;
- обязательные ключи и `ValueGeneratedNever` для идентификаторов приложения;
- длины строк, индексы и уникальность связей;
- явное удаление контейнеров перед сервером в одной транзакции и каскадное удаление зависимых permissions и notification bindings;
- составные ключи, запрещающие доступ к контейнеру вне назначенного сервера;
- правило notification binding «ровно один target»;
- хранение времён как UTC в столбцах SQL Server `datetime2`;
- явный `Version` серверов и контейнеров как concurrency token;
- таблицу `notification_outbox_jobs` с JSON payload в `nvarchar(max)`, attempt, next-attempt, lease и terminal timestamps без внешнего ключа к удаляемому объекту.

Для идемпотентности уведомлений объект хранит nullable `CurrentIncidentId`, а каждая привязка — nullable идентификаторы инцидента успешной outage- и recovery-доставки. Глобальный `OutageNotificationSent` удалён из кодовой модели, потому что не описывает частичный успех нескольких чатов.

`UtcDateTimeOffsetConverter` преобразует `DateTimeOffset` в UTC `DateTime` провайдера и восстанавливает нулевое смещение при чтении.

## EFCoreLibrary

Локальная `libs/EFCoreLibrary.dll` подключена только к Infrastructure. `AddEfCoreContext<UptimeHarborDbContext, UptimeHarborDbContext>()` регистрирует scoped adapter; `AddEfCoreBaseRepositories<UptimeHarborDbContext>()` — базовые create/delete/predicate/query repositories. Реализации внедряют `IContext*Repository<TEntity, UptimeHarborDbContext>`; один и тот же тип используется как EF-контекст и ключ adapter. Версии зависимостей, источники DLL и порядок обновления описаны в [Libraries.md](Libraries.md).

Предметные репозитории адаптируют их к Application-контрактам. Для поиска по ID используется predicate repository, поэтому Domain не реализует библиотечный `IEntity<TId>`.

Все внешние списковые read-методы Application repositories возвращают `Task<IReadOnlyList<T>>`. Infrastructure по-прежнему materialize-ит запросы в конкретные `List<T>` через базовые list operations или `ToListAsync`; изменяемый тип не выходит через контракт. Запросы чтения выполняются no-tracking, запрос объекта для последующего изменения остаётся tracked. `IQueryable` не выходит из Infrastructure.

`DashboardSnapshotReadQuery` отдельно проецирует server/container dashboard records без endpoint URL и permission IDs текущего пользователя. Для сервера проекция содержит заданное администратором имя и отдельно последнее внутреннее имя из endpoint. `MonitoringRecipientReadQuery` отдельно materialize-ит users с ролью Administrator и пакетные permission ID обычных пользователей: один server-permission query и, только для container event, один container-permission query. Оба контракта находятся в Application, реализации — в Infrastructure; EF entities, provider-типы и `IQueryable` наружу не выходят.

Для начала каждого monitoring cycle `GetEnabledAsync` фильтрует включённые серверы и контейнеры в БД и возвращает materialized no-tracking snapshot. Результат каждой параллельной проверки записывается в отдельном DI scope: объект повторно загружается tracked и сохраняется через scoped `IMonitoringUnitOfWork`.

`Version` увеличивается доменными методами. EF добавляет исходное значение в `UPDATE`/`DELETE`; нулевое число затронутых строк даёт `DbUpdateConcurrencyException`. Технический Unit of Work очищает stale change tracker и переводит исключение в независимый `PersistenceConcurrencyException`. Monitoring считает его устаревшим результатом без событий, а административный use case возвращает semantic conflict.

`MonitoredServer.Name` обязателен и ограничен общей максимальной длиной имени объекта. Уникальный индекс `ux_monitored_servers_name` построен по сохраняемому `NormalizedName`, поэтому регистр не позволяет обойти запрет повторов независимо от collation базы. Application заранее отклоняет такое совпадение, а SQL Server unique violation 2601/2627 переводится в `PersistenceConflictKind.DuplicateServerName`, чтобы конкурентное создание или переименование возвращало ожидаемый conflict.

Тот же технический Unit of Work отдельно классифицирует только SQL Server unique violations 2601/2627 для EF entries `TelegramChat` и `NotificationBinding`. Они преобразуются в provider-independent `PersistenceConflictException`; Application не зависит от EF Core или `Microsoft.Data.SqlClient`. Неизвестный номер, неизвестная сущность и произвольный `DbUpdateException` пробрасываются как непредвиденная ошибка. Unique indexes Telegram chat ID и пар chat/target имеют стабильные имена, совпадающие с SQL Server baseline.

## Сценарные Unit of Work

Application определяет общий `IUnitOfWorkScope` только с `SaveChangesAsync` и выполнением действия в транзакции.

Сценарные интерфейсы:

- `IMonitoringUnitOfWork`;
- `IObjectAdministrationUnitOfWork`;
- `IVisibilitySettingsUnitOfWork`;
- `INotificationSettingsUnitOfWork`.

Infrastructure использует одну scoped-сессию `IUptimeHarborDbContextSession`. `UptimeHarborDbUnitOfWork` реализует сохранение и транзакцию, а сценарные классы добавляют только нужные репозитории.

`IObjectAdministrationUnitOfWork` раскрывает только серверы и контейнеры и отдельную предметную операцию отмены recovery. Отключение мониторинга или смена endpoint атомарно фиксирует сброс объекта и отмену незавершённых recovery jobs прежнего incident. Отмена выполняется условным update существующих `PayloadJson`/`CompletedAtUtc`, сохраняет job и явную диагностическую причину и не меняет схему. Повторная отмена безопасна. Удаление сервера выполняется транзакционно после повторной проверки подтверждённого количества контейнеров: контейнеры явно помечаются на удаление перед сервером, а их permissions и notification bindings удаляются каскадными внешними ключами. Связь server → containers использует `NoAction`, чтобы схема SQL Server не содержала запрещённых multiple cascade paths.

Транзакционный вызов открывает транзакцию, выполняет действие, один раз сохраняет изменения и делает commit. При исключении выполняется rollback, после чего исходное исключение передаётся вызывающему коду; отдельная ошибка rollback сохраняется в `Exception.Data` и не заменяет первопричину.

Identity store использует тот же scoped `UptimeHarborDbContext`, что позволяет удалению пользователя и его permissions выполняться внутри сценарной транзакции видимости без глобального Unit of Work.

`IMonitoringUnitOfWork` атомарно сохраняет состояние объекта и outage/recovery outbox job. Отдельный dispatcher claim-ит due jobs коротким lease. Непосредственно перед sender он условно захватывает активную строку job в транзакции и удерживает её до terminal/retry update: это сериализует отправку с административной отменой. Подготовка списка Telegram-доставок выполняет no-tracking чтение привязок. После успешного transport-вызова конкретная привязка повторно загружается tracked и сохраняется через отдельный scope `INotificationSettingsUnitOfWork`, поэтому состояние одного получателя не объединяется с результатом другого.

## Миграции

Pending migrations применяются автоматически при старте единственного Web-процесса после проверки подключения и успешного SQL Server backup. Миграции завершаются до Identity bootstrap; ошибка останавливает запуск. Порядок создания и применения описан в [Database migrations.md](Database%20migrations.md).

Для design-time scaffolding используется `UptimeHarborDesignTimeDbContextFactory`. Она настраивает провайдер SQL Server без строки подключения: построение модели и генерация миграции не требуют секрета и не открывают соединение с БД.

Прежняя цепочка PostgreSQL-миграций удалена как несовместимая с новым провайдером. Создана новая baseline-миграция SQL Server `20260831015604_InitialSqlServerSchema`; она включает текущую модель целиком, включая Identity, Data Protection, concurrency columns и durable outbox. Recovery cancellation повторно использует существующие outbox columns и новой миграции не требует. Baseline не применялась и SQL Server runtime-поведение транзакционного gate/unique classifier не проверялось в этой задаче.
