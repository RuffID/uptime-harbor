# Миграции PostgreSQL

## Принятый способ применения

UptimeHarbor не применяет миграции автоматически при запуске. Схема PostgreSQL обновляется отдельным шагом развёртывания до запуска новой версии приложения. Это исключает одновременное изменение схемы несколькими процессами и не скрывает ошибку миграции за ошибкой старта Web-приложения.

## Первая миграция

Миграция создаётся с именем `InitialPostgreSqlSchema`:

```powershell
dotnet ef migrations add InitialPostgreSqlSchema --project .\UptimeHarbor.Infrastructure\UptimeHarbor.Infrastructure.csproj --startup-project .\UptimeHarbor.Web\UptimeHarbor.Web.csproj --output-dir Persistence\Migrations
```

Design-time factory настраивает Npgsql без строки подключения, поэтому команда не требует локального секрета и не подключается к PostgreSQL. Миграция должна включать доменные таблицы, ASP.NET Core Identity и таблицу ключей Data Protection. Сгенерированные файлы необходимо проверить перед применением; вручную они не редактируются.

Первая миграция создана: `Persistence/Migrations/20260825121410_InitialPostgreSqlSchema.cs`. Проверка сгенерированной модели подтвердила наличие Identity, доменных таблиц, каскадов, составных внешних ключей permissions и `data_protection_keys`.

## Миграция состояния доставки уведомлений

Миграция `20260825154441_AddNotificationDeliveryState` заменяет глобальный `OutageNotificationSent` на `CurrentIncidentId` объекта и per-binding поля `OutageDeliveredIncidentId`/`RecoveryDeliveredIncidentId`. Сгенерированные migration и snapshot проверены без ручного редактирования.

## Обновление production

1. Создать резервную копию PostgreSQL.
2. Остановить текущий контейнер приложения, оставив PostgreSQL запущенной.
3. Применить проверенную миграцию отдельной release-командой или EF Core migration bundle.
4. При успешном обновлении схемы запустить новую версию приложения.
5. При ошибке не запускать новую версию и восстановить согласованное состояние согласно плану отката конкретной миграции.

Строка подключения передаётся через внешний JSON-файл и не включается в команду, журнал или Docker image.
