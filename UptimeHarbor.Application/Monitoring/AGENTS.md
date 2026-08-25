# Application monitoring

## Ответственность

Каталог содержит независимые контракты проверки server/container endpoint, DTO результатов и целей, application use case сохранения результата и контракт внутреннего события мониторинга.

## Правила

- Не использовать `HttpClient`, JSON transport, hosted services, EF Core и Web-типы.
- Ожидаемый отказ отдельного endpoint передавать как `EndpointCheckFailureKind`, а неожиданные системные ошибки не преобразовывать в успешный или запасной результат.
- Перед сохранением повторно проверять, что объект включён и его endpoint не изменился с начала проверки.
- Изменять состояние только доменными методами и сохранять через `IMonitoringUnitOfWork`.
- Публиковать `MonitoringStateChangedEvent` только после успешного `SaveChangesAsync`.
- Передавать в событии `CurrentIncidentId`, чтобы downstream handlers могли идемпотентно обработать частичную доставку.
- При изменении контракта обновлять тесты и `Documentation/Technical/Monitoring.md`.
