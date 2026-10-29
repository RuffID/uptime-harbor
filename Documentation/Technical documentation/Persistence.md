# Persistence и транзакции

## Граница кластерного режима

Этап 03 хранит Raft WAL/term/vote/configuration на отдельном диске через DotNext 6.10.0; SQL остаётся восстанавливаемой проекцией. `ClusterStateMachine` передаёт committed records независимому recovery/reducer-порту и ждёт фактического apply; `RevisionClusterRecovery`/`RevisionApplyEngine` реализуют SQL replay и установку snapshot. Admission/fencing/checkpoint проверяются до создания WAL, чтение аккаунтов открывается после проверки догона. В enabled-режиме legacy startup migrations/bootstrap пропускаются; автоматического применения схемы новой сетки нет. Migration 04 `20261029075201_AllowUniquenessConflictRevisions` допускает реальную competing revision старше base другой сущности; к БД не применялась. Порядок восстановления, lock и защита от потери файлов — [Cluster runtime.md](Cluster%20runtime.md).

## Текущее состояние

Persistence реализован в `UptimeHarbor.Infrastructure` на EF Core с провайдером Microsoft SQL Server. `UptimeHarbor.Application` содержит только контекстно-независимые интерфейсы.

`UptimeHarborDbContext` наследует Identity DbContext с ключом `Guid` и одновременно реализует контекст хранения ключей Data Protection. В модель входят объекты мониторинга, Telegram-чаты/bindings, прежний локальный outbox и storage общих данных сетки. Ресурсные назначения исключены. Таблицы/контракты нового хранения и границы будущих workers описаны в [Shared cluster storage.md](Shared%20cluster%20storage.md).

## EF-конфигурации

Конфигурации находятся в `Persistence/Configurations` и применяются через `ApplyConfigurationsFromAssembly`.

Они задают:

- отдельные таблицы серверов и контейнеров;
- строковое хранение статуса и check constraints допустимых значений;
- обязательные ключи и `ValueGeneratedNever` для идентификаторов приложения;
- длины строк, индексы и уникальность связей;
- nullable связь контейнера с сервером с `NoAction`: удаление сервера явно снимает связи в одной транзакции, сохраняя контейнеры и их notification bindings;
- отсутствие таблиц и рабочих repository/UoW/API permissions;
- правило notification binding «ровно один target»;
- хранение времён как UTC в столбцах SQL Server `datetime2`;
- явный `Version` серверов и контейнеров как concurrency token;
- таблицу `notification_outbox_jobs` с JSON payload в `nvarchar(max)`, attempt, next-attempt, lease и terminal timestamps без внешнего ключа к удаляемому объекту.

Для идемпотентности уведомлений объект хранит nullable `CurrentIncidentId`, а каждая привязка — nullable идентификаторы инцидента успешной outage- и recovery-доставки. Глобальный `OutageNotificationSent` удалён из кодовой модели, потому что не описывает частичный успех нескольких чатов.

`UtcDateTimeOffsetConverter` преобразует `DateTimeOffset` в UTC `DateTime` провайдера и восстанавливает нулевое смещение при чтении.

`ObjectAdministrationService` сохраняет настройки сервера и `ContainerLinkUpdate` одним commit. Проверяются версии всех переданных контейнеров и полнота текущих связей; контейнер другого сервера через этот список не захватывается. Изменение/снятие связи увеличивает версии контейнера и обоих затронутых серверов. Compatibility cleanup permissions удалён.

## EFCoreLibrary

Локальная `libs/EFCoreLibrary.dll` подключена только к Infrastructure. `AddEfCoreContext<UptimeHarborDbContext, UptimeHarborDbContext>()` регистрирует scoped adapter; `AddEfCoreBaseRepositories<UptimeHarborDbContext>()` — базовые create/delete/predicate/query repositories. Реализации внедряют `IContext*Repository<TEntity, UptimeHarborDbContext>`; один и тот же тип используется как EF-контекст и ключ adapter. Версии зависимостей, источники DLL и порядок обновления описаны в [Libraries.md](Libraries.md).

Предметные репозитории адаптируют их к Application-контрактам. Для поиска по ID используется predicate repository, поэтому Domain не реализует библиотечный `IEntity<TId>`.

Все внешние списковые read-методы Application repositories возвращают `Task<IReadOnlyList<T>>`. Infrastructure по-прежнему materialize-ит запросы в конкретные `List<T>` через базовые list operations или `ToListAsync`; изменяемый тип не выходит через контракт. Запросы чтения выполняются no-tracking, запрос объекта для последующего изменения остаётся tracked. `IQueryable` не выходит из Infrastructure.

`DashboardSnapshotReadQuery` проецирует server/container records без endpoint URL: администратору доступны все, неадминистративный compatibility-вызов получает пустой набор. `MonitoringRecipientReadQuery` materialize-ит пользователей/роль одним запросом, без permissions и N+1. Legacy visibility policy предоставляет доступ только Administrator; EF entities, provider-типы и `IQueryable` наружу не выходят.

Для начала каждого monitoring cycle `GetEnabledAsync` фильтрует включённые серверы и контейнеры в БД и возвращает materialized no-tracking snapshot. Результат каждой параллельной проверки записывается в отдельном DI scope: объект повторно загружается tracked и сохраняется через scoped `IMonitoringUnitOfWork`.

`Version` увеличивается доменными методами. EF добавляет исходное значение в `UPDATE`/`DELETE`; нулевое число затронутых строк даёт `DbUpdateConcurrencyException`. Технический Unit of Work очищает stale change tracker и переводит исключение в независимый `PersistenceConcurrencyException`. Monitoring считает его устаревшим результатом без событий, а административный use case возвращает semantic conflict.

`MonitoredServer.Name` обязателен и ограничен общей максимальной длиной имени объекта. Уникальный индекс `ux_monitored_servers_name` построен по сохраняемому `NormalizedName`, поэтому регистр не позволяет обойти запрет повторов независимо от collation базы. Application заранее отклоняет такое совпадение, а SQL Server unique violation 2601/2627 переводится в `PersistenceConflictKind.DuplicateServerName`, чтобы конкурентное создание или переименование возвращало ожидаемый conflict.

Тот же технический Unit of Work отдельно классифицирует только SQL Server unique violations 2601/2627 для EF entries `TelegramChat` и `NotificationBinding`. Они преобразуются в provider-independent `PersistenceConflictException`; Application не зависит от EF Core или `Microsoft.Data.SqlClient`. Неизвестный номер, неизвестная сущность и произвольный `DbUpdateException` пробрасываются как непредвиденная ошибка. Unique indexes Telegram chat ID и пар chat/target имеют стабильные имена, совпадающие с SQL Server baseline.

## Snapshot и повтор применения

`RevisionWriteSetFactory` формирует ready projection changes; `ProjectionApplyUnitOfWork` одной транзакцией проверяет receipt/cursor и фиксирует аккаунт/роль, настройки, decisions/controls/conflicts/tombstones и позицию. Immutable accepted image публикуется после commit. Повтор после потерянного ответа сверяется по digest и возвращает исходное решение; SQL receipt без payload не является WAL.

`SnapshotProjectionUnitOfWork` экспортирует shared whitelist с проверенным cursor. Install валидирует образ до SQL, отвергает stale account/cursor, удаляет/устанавливает общие rows и checkpoint одной внешней транзакцией (два SaveChanges, один commit). Локальные user lockout counters, SessionEpoch, сессии, ключи, секреты и Pending сохраняются. Соответствие SQL transaction semantics не доказано fake-тестами; реальная provider-проверка не проводилась.

## Сценарные Unit of Work

Application определяет общий `IUnitOfWorkScope` только с `SaveChangesAsync` и выполнением действия в транзакции.

Сценарные интерфейсы:

- `IMonitoringUnitOfWork`;
- `IObjectAdministrationUnitOfWork`;
- `INotificationSettingsUnitOfWork`.
- `IProjectionApplyUnitOfWork`, `IPendingControlUnitOfWork`, `IObservationCaptureUnitOfWork`, `IDeliveryResultUnitOfWork` — независимые транзакционные границы общего storage и локального capture.

Infrastructure использует одну scoped-сессию `IUptimeHarborDbContextSession`. `UptimeHarborDbUnitOfWork` реализует сохранение и транзакцию, а сценарные классы добавляют только нужные репозитории.

`IObjectAdministrationUnitOfWork` сохраняет серверы/контейнеры и отдельную предметную отмену recovery. Отключение мониторинга или смена endpoint атомарно фиксирует сброс объекта и отмену незавершённых прежних recovery jobs. Удаление сервера после проверки количества контейнеров отвязывает их, сохраняя контейнеры и bindings. Связь server → containers использует `NoAction`; метод cleanup permissions оставлен как совместимость без хранения.

Транзакционный вызов открывает транзакцию, выполняет действие, один раз сохраняет изменения и делает commit. При исключении выполняется rollback, после чего исходное исключение передаётся вызывающему коду; отдельная ошибка rollback сохраняется в `Exception.Data` и не заменяет первопричину.

Identity использует тот же scoped `UptimeHarborDbContext`. Новый projection adapter переносит account/роль/версию без UserManager autocommit и сохраняет их с cursor одной транзакцией. Прежние пользовательские use case пока не переключены на журнал; общий вход остаётся этапом 05.

`IMonitoringUnitOfWork` атомарно сохраняет состояние объекта и outage/recovery outbox job. Отдельный dispatcher claim-ит due jobs коротким lease. Непосредственно перед sender он условно захватывает активную строку job в транзакции и удерживает её до terminal/retry update: это сериализует отправку с административной отменой. Подготовка списка Telegram-доставок выполняет no-tracking чтение привязок. После успешного transport-вызова конкретная привязка повторно загружается tracked и сохраняется через отдельный scope `INotificationSettingsUnitOfWork`, поэтому состояние одного получателя не объединяется с результатом другого.

## Миграции

Для этапа 02 создана `20261029053354_AddSharedClusterStorage` с designer и обновлённым snapshot: новые storage-таблицы, ревизии и аккаунтные поля, исключение resource assignments. Предыдущие migrations сохранены. Snapshot соответствует текущей SQL Server design-time модели по изолированному model-diff тесту; реального применения/отката не было. Новая схема рассчитана на первый запуск без v1/backfill. Полная запись — [Database migrations.md](Database%20migrations.md).

Для необязательного родителя создана migration `20261018104849_MakeContainerServerOptional`: удаляются составной FK permission → (ServerId, Id) и соответствующий alternate key контейнера, ServerId становится nullable, создаются обычный server index и FK permission → ContainerId. Существующие связи и данные сохраняются; designer фиксирует модель того этапа, текущий snapshot уже включает `AddSharedClusterStorage`. Down требует предварительно связать контейнеры с null ServerId с существующими серверами: сгенерированный default Guid.Empty удалён по прямому разрешению пользователя, автоматического удаления данных нет. Миграция к БД не применялась.


Pending migrations применяются автоматически при старте единственного Web-процесса после проверки подключения и успешного SQL Server backup. Миграции завершаются до Identity bootstrap; ошибка останавливает запуск. Порядок создания и применения описан в [Database migrations.md](Database%20migrations.md).

Для design-time scaffolding используется `UptimeHarborDesignTimeDbContextFactory`. Она настраивает провайдер SQL Server без строки подключения: построение модели и генерация миграции не требуют секрета и не открывают соединение с БД.

Прежняя цепочка PostgreSQL-миграций удалена как несовместимая с новым провайдером. Baseline SQL Server `20260831015604_InitialSqlServerSchema` фиксирует исходную модель, включая Identity, Data Protection, concurrency columns и durable outbox; дальнейшие изменения добавлены отдельными migrations. Recovery cancellation повторно использует существующие outbox columns и новой миграции не требует. Baseline не применялась и SQL Server runtime-поведение транзакционного gate/unique classifier не проверялось в этой задаче.
