# Persistence и транзакции

## Текущее состояние

Persistence реализован в `UptimeHarbor.Infrastructure`. `UptimeHarbor.Application` содержит только контекстно-независимые интерфейсы.

`UptimeHarborDbContext` наследует Identity DbContext с ключом `Guid` и одновременно реализует контекст хранения ключей Data Protection. В модель входят объекты мониторинга, permissions, Telegram-чаты и notification bindings.

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
- хранение времён как UTC.

`UtcDateTimeOffsetConverter` преобразует `DateTimeOffset` в UTC `DateTime` провайдера и восстанавливает нулевое смещение при чтении.

## EFCoreLibrary

Локальная `libs/EFCoreLibrary.dll` подключена только к Infrastructure. Через `AddEfCoreBaseRepositories<UptimeHarborDbContext>()` регистрируются базовые create/delete/predicate/query repositories.

Предметные репозитории адаптируют их к Application-контрактам. Для поиска по ID используется predicate repository, поэтому Domain не реализует библиотечный `IEntity<TId>`.

Запросы чтения списков выполняются no-tracking. Запрос объекта для последующего изменения остаётся tracked. `IQueryable` не выходит из Infrastructure.

## Сценарные Unit of Work

Application определяет общий `IUnitOfWorkScope` только с `SaveChangesAsync` и выполнением действия в транзакции.

Сценарные интерфейсы:

- `IMonitoringUnitOfWork`;
- `IVisibilitySettingsUnitOfWork`;
- `INotificationSettingsUnitOfWork`.

Infrastructure использует одну scoped-сессию `IUptimeHarborDbContextSession`. `UptimeHarborDbUnitOfWork` реализует сохранение и транзакцию, а сценарные классы добавляют только нужные репозитории.

Транзакционный вызов открывает транзакцию, выполняет действие, один раз сохраняет изменения и делает commit. При исключении выполняется rollback, после чего исходное исключение передаётся вызывающему коду.

## Миграции

Миграции не применяются автоматически при старте. Порядок создания и применения описан в [Database migrations.md](Database%20migrations.md).

Первая миграция ещё не создана, поэтому этап данных остаётся в статусе «В работе».
