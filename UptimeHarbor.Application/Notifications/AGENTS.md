# Application notifications

## Ответственность

Каталог содержит независимую абстракцию текстовой доставки, административные use case, подготовку сообщений и фиксацию per-binding результата текущего инцидента.

## Правила

- Enabled test-send передавать через IAcceptedTestDelivery в durable Pending, после accepted Administrator policy. Повторная авторизация выполняется reducer и перед transport; фоновые accepted intents не требуют пользовательской сессии. Legacy sender не использовать для обхода cluster gate.

- Не использовать Telegram SDK, `HttpClient`, EF Core, MVC, Razor Pages или logging transport.
- Ожидаемые ошибки sender передавать через `NotificationDeliveryException` с `NotificationDeliveryFailure`; тестовая отправка возвращает безопасный `UserMessage` с причиной и действием. Произвольный `Exception.Message` не раскрывать.
- Проверять administrator access через единый `IAdministratorAccessPolicy` внутри каждого административного use case до чтения, сохранения и тестовой отправки.
- Outage выбирать только для привязок без успешного маркера текущего incident ID; recovery — только после успешного outage той же привязки.
- Формировать DTO и текст сообщения из сохранённого `MonitoringStateChangedEvent`; UTC и uptime получать через `TimeProvider`.
- Контейнер без сервера остаётся допустимой целью; текст доставки не требует родителя и не добавляет строку родительского сервера.
- Серверы в административном списке целей и родительский сервер контейнера показываются по `MonitoredServer.Name`; имя самого проверенного объекта из события остаётся внутренним именем endpoint.
- Повторная outbox-обработка должна выбирать только привязки без соответствующего successful marker; внешний Telegram transport имеет at-least-once, а не exactly-once семантику.
- Административные duplicate chat/binding после persistence race преобразовывать из независимого `PersistenceConflictException` в безопасный `ServiceResult.Conflict` без provider details.
- Recovery job, отменённая при disable/endpoint change, не должна доходить до подготовки получателей или sender; отмена хранится в Infrastructure outbox.
- При изменении контракта обновлять тесты и `Documentation/Technical documentation/Notifications.md`.
