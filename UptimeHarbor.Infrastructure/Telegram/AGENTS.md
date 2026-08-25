# Infrastructure Telegram

## Ответственность

Каталог содержит `TelegramOptions`, HTTP transport Bot API и изолирующий monitoring event handler.

## Правила

- Получать единственный token только через `IOptions<TelegramOptions>`.
- Не логировать request URI, token, response body и текст сообщения; стандартное HTTP-логирование клиента отключать.
- Ошибку одного получателя не передавать monitoring worker и не прекращать обработку остальных.
- После transport-успеха фиксировать конкретную привязку в отдельном DI scope.
- HTTP adapter тестировать через fake `HttpMessageHandler`, не обращаясь к Telegram.
