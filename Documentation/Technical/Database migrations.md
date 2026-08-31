# Миграции SQL Server

## Принятый способ применения

UptimeHarbor не применяет миграции автоматически при запуске. Схема SQL Server обновляется отдельным шагом развёртывания до запуска новой версии приложения. Это исключает одновременное изменение схемы несколькими процессами и не скрывает ошибку миграции за ошибкой старта Web-приложения.

## Baseline после смены провайдера

Прежние migration-файлы PostgreSQL удалены: сгенерированную цепочку одного EF Core provider нельзя применять к другому provider или вручную переписывать как SQL Server migration history.

Baseline-миграция создана с именем `InitialSqlServerSchema`:

```powershell
dotnet ef migrations add InitialSqlServerSchema --project .\UptimeHarbor.Infrastructure\UptimeHarbor.Infrastructure.csproj --startup-project .\UptimeHarbor.Web\UptimeHarbor.Web.csproj --output-dir Persistence\Migrations
```

Design-time factory настраивает SQL Server provider без строки подключения, поэтому команда не требует локального секрета и не подключается к БД. Созданная миграция `Persistence/Migrations/20260831015604_InitialSqlServerSchema.cs` включает доменные таблицы, ASP.NET Core Identity, таблицу ключей Data Protection, concurrency columns и durable notification outbox. Сгенерированные migration и snapshot необходимо проверить перед применением; вручную они не редактируются.

Baseline рассчитана на пустую базу SQL Server. Если в прежней PostgreSQL уже есть данные, их перенос выполняется отдельным проверяемым export/transform/import процессом; EF migration не переносит данные между разными СУБД.

## Обновление production

1. Создать резервную копию SQL Server.
2. Остановить текущий экземпляр приложения, оставив SQL Server доступным для release-команды.
3. Применить проверенную миграцию отдельной release-командой или EF Core migration bundle.
4. При успешном обновлении схемы запустить новую версию приложения.
5. При ошибке не запускать новую версию и восстановить согласованное состояние согласно плану отката конкретной миграции.

Строка подключения `ConnectionStrings:SqlServer` передаётся через внешнюю конфигурацию и не включается в команду, журнал или Docker image.
