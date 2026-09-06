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

Добавление административного имени сервера изменяет схему после baseline: требуются обязательные колонки `Name` и `NormalizedName` в `monitored_servers`, а также уникальный индекс `ux_monitored_servers_name` по нормализованному значению. Новую миграцию следует создать отдельно с именем `AddMonitoredServerName`; существующую baseline-миграцию и snapshot вручную не редактировать. Если таблица уже содержит серверы, до применения обязательных колонок миграция должна определить явную стратегию заполнения уникальных имён для существующих записей.

Baseline рассчитана на пустую базу SQL Server. Если в прежней PostgreSQL уже есть данные, их перенос выполняется отдельным проверяемым export/transform/import процессом; EF migration не переносит данные между разными СУБД.

## Обновление production

1. Остановить прежний экземпляр и убедиться, что новый Web запускается в одном экземпляре.
2. Обеспечить SQL Server доступность и права на создание backup в server-side каталоге.
3. Запустить новую версию Web: она создаст backup и применит pending migrations до открытия HTTP pipeline.
4. Проверить успешный startup и readiness; только затем разрешать трафик или масштабирование.
5. При ошибке startup не повторять запуск параллельно и восстановить согласованное состояние согласно backup/rollback-плану конкретной migration.

Строка подключения `ConnectionStrings:SqlServer` передаётся через внешнюю конфигурацию и не включается в команду, журнал или Docker image.

В рамках изолированной проверки startup service не подключался к SQL Server, backup и baseline не выполнялись. Не проверены server-side backup path/permissions, реальное создание индексов, выдача 2601/2627, EF entries provider exception, блокировка outbox row во время sender и порядок commit при конкурентной административной отмене.
