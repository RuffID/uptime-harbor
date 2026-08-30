# Application notifications

## Ответственность

Каталог содержит независимую абстракцию текстовой доставки, административные use case, подготовку сообщений и фиксацию per-binding результата текущего инцидента.

## Правила

- Не использовать Telegram SDK, `HttpClient`, EF Core, MVC, Razor Pages или logging transport.
- Проверять administrator access через единый `IAdministratorAccessPolicy` внутри каждого административного use case до чтения, сохранения и тестовой отправки.
- Outage выбирать только для привязок без успешного маркера текущего incident ID; recovery — только после успешного outage той же привязки.
- Формировать DTO и текст сообщения из сохранённого `MonitoringStateChangedEvent`; UTC и uptime получать через `TimeProvider`.
- Повторная outbox-обработка должна выбирать только привязки без соответствующего successful marker; внешний Telegram transport имеет at-least-once, а не exactly-once семантику.
- При изменении контракта обновлять тесты и `Documentation/Technical/Notifications.md`.
