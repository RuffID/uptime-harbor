# Application monitoring

## Ответственность

Каталог содержит независимые контракты проверки server/container endpoint, DTO результатов и целей, application use case сохранения результата и контракт внутреннего события мониторинга.

## Правила

- Не использовать `HttpClient`, JSON transport, hosted services, EF Core и Web-типы.
- Ожидаемый отказ отдельного endpoint передавать как `EndpointCheckFailureKind`, а неожиданные системные ошибки не преобразовывать в успешный или запасной результат.
- Имя из успешного ответа считать внутренним именем объекта; административное `MonitoredServer.Name` monitoring use case не изменяет.
- Время запуска сервера передавать из успешного результата в домен и сохранённое событие; не заменять отсутствующее значение временем проверки.
- Перед сохранением повторно проверять, что объект включён и его endpoint не изменился с начала проверки.
- Изменять состояние только доменными методами и сохранять через `IMonitoringUnitOfWork`.
- Публиковать `MonitoringStateChangedEvent` только после успешного `SaveChangesAsync`.
- Outage/recovery-событие добавлять в durable notification outbox до того же `SaveChangesAsync`; конфликт конкурентности считать устаревшим результатом и не публиковать никакое событие.
- Передавать в событии `CurrentIncidentId`, чтобы downstream handlers могли идемпотентно обработать частичную доставку.
- При изменении контракта обновлять тесты и `Documentation/Technical documentation/Monitoring.md`.
