# Application notifications

## Ответственность

Каталог содержит независимую абстракцию текстовой доставки, административные use case, подготовку сообщений и фиксацию per-binding результата текущего инцидента.

## Правила

- Не использовать Telegram SDK, `HttpClient`, EF Core, MVC, Razor Pages или logging transport.
- Проверять роль администратора внутри каждого административного use case.
- Outage выбирать только для привязок без успешного маркера текущего incident ID; recovery — только после успешного outage той же привязки.
- Формировать DTO и текст сообщения из сохранённого `MonitoringStateChangedEvent`; UTC и uptime получать через `TimeProvider`.
- При изменении контракта обновлять тесты и `Documentation/Technical/Notifications.md`.
