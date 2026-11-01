# Миграции SQL Server

## Время загрузки сервера

Создана migration `20261101102504_AddMonitoredServerStartedAtUtc`, designer и model snapshot обновлены штатным EF tooling. `Up` добавляет только nullable колонку `StartedAtUtc` типа `datetime2` в `monitored_servers`, без default и backfill; `Down` удаляет только эту колонку. Существующим серверам нельзя приписывать время развёртывания или текущую дату: поле остаётся `NULL` до успешной проверки нового endpoint. Accepted observer metadata уже имеет своё поле времени запуска и не требует расширения.

К БД migration не применялась; до запуска изменённой версии схему нужно обновить отдельной разрешённой операцией. Подключение к БД и её обновление требуют отдельного поручения. Откат удаляет сохранённые значения времени загрузки серверов.

## Эксплуатация этапа 09

Новой migration нет. `20261029053354_AddSharedClusterStorage` и `20261029075201_AllowUniquenessConflictRevisions` подготовлены ранее, к БД в этой задаче не применялись. ClusterDatabaseProbe проверяет CanConnect и отсутствие pending migrations перед readiness, сам миграции не применяет. Startup migration/backup — отдельное внешнее действие с разрешением; оно не даёт admission/activation/enable. Rollback учитывает всю принятую историю и поддержку новых команд бинарником; downgrade одной SQL-проекции в работающей сетке не поддерживается. [Порядок обслуживания](Cluster%20operations.md#обслуживание-обновление-и-rollback).

## Схема этапа 08

Интерфейс/read DTO и controls используют существующие таблицы и protected Pending; новая migration не требуется. Historical migrations/designer/model snapshot не изменялись. Подготовленные 20261029053354_AddSharedClusterStorage и 20261029075201_AllowUniquenessConflictRevisions к БД в этой задаче не применялись.


## Схема этапа 07

Production attempts/results, retry deadline, test actor, recovery entitlement и immutable summaries используют существующие таблицы/поля схемы 02. Изменён accepted image/projection contract, новая EF migration не требуется. Исторические migrations, generated designer/model snapshot не редактировались; 20261029053354_AddSharedClusterStorage и 20261029075201_AllowUniquenessConflictRevisions к БД не применялись.

БД/SQL, включая SQLite, и tooling создания migration не запускались. Проверки projection/snapshot metadata с закрытым connection не являются runtime проверкой схемы. [Проверки](Testing.md#проверка-этапа-07).

## Схема после этапа 06

Production capture, accepted observations/frontiers, incidents/events/intents и состав сводок используют таблицы этапа 02. Изменения image/command contracts не требуют нового SQL столбца. Новая migration не создавалась, historical migrations и generated designer/snapshot не редактировались. Обе подготовленные migrations 02/04 к БД не применялись; SQL/SQLite и runtime DDL не запускались. Enabled startup не включает automatic migration.

## Принятый способ применения

В Disabled UptimeHarbor автоматически применяет pending migrations при старте Web. `InitializeInfrastructureAsync` создаёт scope и выполняет `DataBaseCheckUpService<UptimeHarborDbContext>` до legacy bootstrap Identity:

1. проверяет доступность SQL Server;
2. получает список pending migrations;
3. если список не пуст, создаёт SQL Server backup;
4. вызывает `MigrateAsync`;
5. только после успеха создаёт роли и bootstrap-администратора.

Database startup ограничен 60 секундами. Ошибка подключения, backup, migration или timeout останавливает запуск; fallback с пропуском миграций отсутствует. Connection string, SQL command и backup path не логируются, имена pending migrations логируются.

Механизм рассчитан на один Web-процесс в Disabled. В Bootstrap/Join/Restart `InitializeInfrastructureAsync` возвращается до этой процедуры: схему каждой SQL-проекции подготавливает отдельно разрешённая операция окружения без запуска legacy bootstrap. ClusterDatabaseProbe проверяет connectivity и отсутствие pending migrations, но не применяет их. Координация конкурентного применения миграций несколькими экземплярами не реализована; подготовку схемы сериализовать и не включать workers до её подтверждения.

Перед migration backup создаётся через `BACKUP DATABASE`. На Linux используется SQL Server path `/var/opt/mssql/backups`, на остальных платформах — каталог `Backups` рядом с исполняемым Web-приложением. Путь интерпретируется SQL Server host; его service account должен иметь право записи. На Windows приложение создаёт локальный каталог, но для удалённого SQL Server оператор должен обеспечить соответствующий доступный server-side path.

## Baseline после смены провайдера

Прежние migration-файлы PostgreSQL удалены: сгенерированную цепочку одного EF Core provider нельзя применять к другому provider или вручную переписывать как SQL Server migration history.

Baseline-миграция создана с именем `InitialSqlServerSchema`:

```powershell
dotnet ef migrations add InitialSqlServerSchema --project .\UptimeHarbor.Infrastructure\UptimeHarbor.Infrastructure.csproj --startup-project .\UptimeHarbor.Web\UptimeHarbor.Web.csproj --output-dir Persistence\Migrations
```

Design-time factory настраивает SQL Server provider без строки подключения, поэтому команда не требует локального секрета и не подключается к БД. Созданная миграция `Persistence/Migrations/20260831015604_InitialSqlServerSchema.cs` включает доменные таблицы, ASP.NET Core Identity, таблицу ключей Data Protection, concurrency columns и durable notification outbox. Сгенерированные migration и snapshot необходимо проверить перед применением; вручную они не редактируются.

Исправление recovery cancellation использует уже существующие `PayloadJson`, `CompletedAtUtc` и `LockedUntilUtc`; новых колонок и отдельной migration для него не требуется. Явные имена unique indexes Telegram chat/binding совпадают с именами, уже созданными baseline. Файлы `20260831015604_InitialSqlServerSchema` и snapshot этим исправлением не изменялись.

Migration `20260906132033_AddMonitoredServerName` уже входит в историю: обязательные `Name`/`NormalizedName` и уникальный `ux_monitored_servers_name`. Исторические файлы не изменялись при подготовке модели сетки.

Baseline рассчитана на пустую базу SQL Server. Если в прежней PostgreSQL уже есть данные, их перенос выполняется отдельным проверяемым export/transform/import процессом; EF migration не переносит данные между разными СУБД.

## Необязательная связь контейнера

Создана migration `20261018104849_MakeContainerServerOptional`, designer и model snapshot обновлены штатным tooling. Миграция удаляет составной внешний ключ container permission и alternate key контейнера, делает `MonitoredContainer.ServerId` nullable, создаёт индекс родителя и заменяет permission FK ссылкой на ID контейнера. Старые связи и данные сохраняются.

Команда выполнена с `--configuration MigrationCheck`, поскольку Debug DLL заняты запущенным приложением. Design-time factory не подключается к БД. Миграция проверена по исходникам, к БД не применялась. Tool `dotnet-ef` 10.0.11 сообщил о более новой версии runtime 10.0.13; генерация завершилась успешно.

Для Down контейнеры без сервера нужно заранее связать с существующими серверами. По прямому разрешению пользователя из Down удалён сгенерированный `defaultValue: Guid.Empty`: при наличии null откат завершится ошибкой вместо подстановки фиктивного родителя. Designer и snapshot вручную не изменялись. Реальное применение и откат на SQL Server не проверялись.

## Обновление production

Эти правила описывают прежний однопроцессный startup. Подготовка схемы сама по себе не разрешает запуск общей модели; реализованные cluster bootstrap/join и эксплуатационные процедуры описаны в [Cluster operations.md](Cluster%20operations.md).

1. Остановить прежний экземпляр и убедиться, что новый Web запускается в одном экземпляре.
2. Обеспечить SQL Server доступность и права на создание backup в server-side каталоге.
3. Запустить новую версию Web: она создаст backup и применит pending migrations до открытия HTTP pipeline.
4. Проверить успешный startup и readiness; только затем разрешать трафик или масштабирование.
5. При ошибке startup не повторять запуск параллельно и восстановить согласованное состояние согласно backup/rollback-плану конкретной migration.

Строка подключения `ConnectionStrings:SqlServer` передаётся через внешнюю конфигурацию и не включается в команду, журнал или Docker image.

В рамках изолированной проверки startup service не подключался к SQL Server, backup и baseline не выполнялись. Не проверены server-side backup path/permissions, реальное создание индексов, выдача 2601/2627, EF entries provider exception, блокировка outbox row во время sender и порядок commit при конкурентной административной отмене.

## Схема общих данных, этап 02

Создана новая `20261029053354_AddSharedClusterStorage`, её designer и согласованный model snapshot. Up удаляет только `container_visibility_permissions` и `server_visibility_permissions`, добавляет раздельные ревизии и storage из [Shared cluster storage.md](Shared%20cluster%20storage.md), ограничивает роли Administrator и нормализованный login. Нет InsertData, выдачи ресурсных прав, отдельного SQL/backfill или переписывания исторических migrations. Down возвращает прежнюю структуру и удаляет новые таблицы; принятую историю он не сохраняет и для рабочего cluster rollback не предназначен.

Scaffolding выполнен через Infrastructure как target/startup project, context `UptimeHarborDbContext`, output `Persistence/Migrations`, configuration `MigrationCheck`. Factory использует `UseSqlServer()` без строки подключения; приложение не запускается. Tool 10.0.11 предупредил о runtime 10.0.13 и возможной потере данных при удалении таблиц. Развёрнутых прежних установок нет, схема предназначена только для первого запуска. Сгенерированные нулевые/пустые значения при Add/AlterColumn не являются поддержкой переноса заполненной установки; её преобразование не предусмотрено.

После проверки модели новая неприменённая migration пересоздана tooling с двумя исправлениями: длина вида tombstone и unique key incident/kind/chat для problem/recovery. Установленный tool не поддерживает `remove --offline`: команда завершилась без правок. Отдельно разрешённый `remove --force` не смог проверить БД из-за отсутствующей ConnectionString, затем восстановил предыдущий snapshot; после этого выполнен повторный add. Исторические migration/designer не менялись, новый designer/snapshot вручную не редактировались.

Изолированные тесты проверили Up/Down operations без генерации SQL, отсутствие data inserts, уникальные индексы и нулевой model diff snapshot/current SQL Server model. Подключения к БД, применения Up/Down, backup/restore и SQL Server atomicity не было. Snapshot/model diff не доказывает, что provider успешно выполнит DDL на реальном SQL Server.

## Конкурирующие ревизии, этап 04

По отдельному разрешению создана `20261029075201_AllowUniquenessConflictRevisions`, designer и согласованный snapshot. Команда:

```powershell
dotnet ef migrations add AllowUniquenessConflictRevisions --project .\UptimeHarbor.Infrastructure\UptimeHarbor.Infrastructure.csproj --startup-project .\UptimeHarbor.Infrastructure\UptimeHarbor.Infrastructure.csproj --context UptimeHarborDbContext --output-dir Persistence/Migrations
```

Изменяется только `ck_shared_conflicts_revisions`: Up требует BaseRevision ≥0 и FirstAcceptedRevision >0; прежнее сравнение FirstAcceptedRevision > BaseRevision неверно для uniqueness conflict двух разных сущностей. Например, competing record принят на 3, изменяемый — на 4: обе реальные версии должны сохраняться. Down возвращает прежний constraint; при существующих таких конфликтах он может завершиться ошибкой и требует предварительного анализа данных, автоматического backfill нет.

Исторические migrations не переписывались. Factory не содержит строки подключения; приложение и БД не запускались. Tool 10.0.11 предупредил о runtime 10.0.13; scaffolding завершился. `SharedStorageMigrationTests` проверяет Up/Down operations и нулевой model/snapshot diff без SQL. Migration к БД не применялась; исполнение DDL/Down на SQL Server не подтверждено.

## Схема после 05.2–05.4

Общий sign-in, account commands, локальные counters/session validation и аккаунтная часть bootstrap/recovery используют существующие поля схемы 02/04. Новая migration не потребовалась; historical migrations, designer и snapshot не изменялись. Обе подготовленные migrations `20261029053354_AddSharedClusterStorage` и `20261029075201_AllowUniquenessConflictRevisions` к БД не применялись. Аккаунтная часть bootstrap реализована отдельно от будущего controlled enable/эксплуатации 09; enabled startup по-прежнему пропускает automatic migration/legacy bootstrap.
