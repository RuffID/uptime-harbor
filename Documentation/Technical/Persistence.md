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
- `IObjectAdministrationUnitOfWork`;
- `IVisibilitySettingsUnitOfWork`;
- `INotificationSettingsUnitOfWork`.

Infrastructure использует одну scoped-сессию `IUptimeHarborDbContextSession`. `UptimeHarborDbUnitOfWork` реализует сохранение и транзакцию, а сценарные классы добавляют только нужные репозитории.

`IObjectAdministrationUnitOfWork` раскрывает только серверы и контейнеры. Обычное сохранение атомарно фиксирует изменение настроек, включая сброс состояния при выключении мониторинга. Удаление сервера выполняется транзакционно после повторной проверки подтверждённого количества контейнеров; контейнеры, permissions и notification bindings удаляются существующими каскадными внешними ключами.

Транзакционный вызов открывает транзакцию, выполняет действие, один раз сохраняет изменения и делает commit. При исключении выполняется rollback, после чего исходное исключение передаётся вызывающему коду; отдельная ошибка rollback сохраняется в `Exception.Data` и не заменяет первопричину.

Identity store использует тот же scoped `UptimeHarborDbContext`, что позволяет удалению пользователя и его permissions выполняться внутри сценарной транзакции видимости без глобального Unit of Work.

## Миграции

Миграции не применяются автоматически при старте. Порядок создания и применения описан в [Database migrations.md](Database%20migrations.md).

Для design-time scaffolding используется `UptimeHarborDesignTimeDbContextFactory`. Она настраивает провайдер Npgsql без строки подключения: построение модели и генерация миграции не требуют секрета и не открывают соединение с PostgreSQL.

Первая миграция `20260825121410_InitialPostgreSqlSchema` создана в `Persistence/Migrations`. Она включает доменные таблицы, ASP.NET Core Identity, составные ограничения видимости и таблицу ключей Data Protection.
