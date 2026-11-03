# Infrastructure monitoring

- ClusterMonitoringCycle проверяет только accepted назначенные цели, до восьми параллельных scopes. Перед capture повторно проверяет конфигурацию и RecoveryVersion; cadence отмечается лишь после durable capture. Условие Disabled не запускает этот путь.

## Ответственность

Каталог содержит HTTP/JSON-адаптер endpoint, параллельный запуск одного цикла и hosted worker периодического мониторинга.

## Правила

- HTTP GET выполнять через `IHttpClientFactory` без заголовка авторизации.
- Для server и container endpoint требовать `name` и `startedAtUtc` с явным UTC offset (`Z` или `+00:00`), не позже проверки. В cluster server capture сохранять это время в observation metadata.
- Timeout и интервал получать только из валидированных `MonitoringOptions`; текущее время и таймеры — через `TimeProvider`.
- Каждый объект проверять и сохранять в отдельном DI scope; не разделять `DbContext` между параллельными задачами.
- Keyed-координацию освобождать в `finally`; одновременно разрешена только одна проверка пары «тип + идентификатор».
- Ожидаемые HTTP, network, timeout и validation-ошибки возвращать как результат проверки; неожиданные исключения изолировать на границе объекта и логировать.
- Не выполнять retry внутри цикла и корректно передавать cancellation при остановке.
- Прежний worker зарегистрирован как управляемый singleton; supervisor запускает его только для обычного Disabled. MonitoringCycleRunner await-ит все проверки и освобождает keyed coordination в finally. Распределённый worker проверяет NodeReadiness.CanWork и подтверждает реальный прогресс через MonitoringProgressWatchdog; partial цели не является неисправностью самого главного.
- Автоматические redirect отключать; любой `3xx` считать HTTP-ошибкой. Тело ответа ограничивать 64 KiB по `Content-Length` и фактически прочитанным байтам.
- При изменении цикла или transport-контракта обновлять тесты и `Documentation/Technical documentation/Monitoring.md`.
