# Миграции SQL Server

## Принятый способ применения

UptimeHarbor автоматически применяет pending migrations при старте Web. `InitializeInfrastructureAsync` создаёт scope и выполняет `DataBaseCheckUpService<UptimeHarborDbContext>` до bootstrap Identity:

1. проверяет доступность SQL Server;
2. получает список pending migrations;
3. если список не пуст, создаёт SQL Server backup;
4. вызывает `MigrateAsync`;
5. только после успеха создаёт роли и bootstrap-администратора.

Database startup ограничен 60 секундами. Ошибка подключения, backup, migration или timeout останавливает запуск; fallback с пропуском миграций отсутствует. Connection string, SQL command и backup path не логируются, имена pending migrations логируются.

Механизм рассчитан на один Web-процесс. Координация конкурентного применения миграций несколькими экземплярами не реализована, поэтому при обновлении нельзя одновременно запускать несколько новых экземпляров.

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

Эти правила описывают прежний однопроцессный startup. Подготовка этапа 02 не разрешает запуск новой общей модели; cluster bootstrap/join и эксплуатационные процедуры ещё не реализованы.

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
