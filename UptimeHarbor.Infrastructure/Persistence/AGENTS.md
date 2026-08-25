# Infrastructure persistence

## Ответственность

Каталог содержит `UptimeHarborDbContext`, EF-конфигурации, реализации репозиториев, scoped-сессию контекста и сценарные Unit of Work.

## Правила

- Все репозитории и Unit of Work в одном DI scope используют один `UptimeHarborDbContext`.
- Модель EF настраивать под защищённую доменную модель, не ослабляя её публичный API.
- Сохранять ограничения, индексы, каскады и UTC-конвертацию согласованными с доменными правилами.
- Существующие миграции вручную не редактировать; новые создаются отдельной командой после изменения модели.
- Design-time scaffolding использует `UptimeHarborDesignTimeDbContextFactory` без строки подключения и не открывает соединение с БД.
- При изменении схемы или транзакционных границ обновлять этот файл, `Documentation/Technical/Persistence.md` и документ миграций.
- Цикл загружает включённые цели no-tracking, а результат каждого объекта повторно загружается tracked и сохраняется через отдельный scoped `IMonitoringUnitOfWork`.
- Per-binding outage/recovery marker сохраняется отдельным scoped `INotificationSettingsUnitOfWork` после успешной transport-доставки; схема закреплена миграцией `AddNotificationDeliveryState`.
