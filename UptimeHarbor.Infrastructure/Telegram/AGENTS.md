# Infrastructure Telegram

## Ответственность

Каталог содержит `TelegramOptions`, HTTP transport Bot API, durable outbox dispatcher и отдельный hosted worker доставки.

## Правила

- Получать единственный token только через `IOptions<TelegramOptions>`.
- Не логировать request URI, token, response body и текст сообщения; стандартное HTTP-логирование клиента отключать.
- Telegram transport не вызывается из monitoring event publisher: monitoring атомарно сохраняет outbox job, а отдельный worker доставляет его позже.
- Получателей и задания обрабатывать с ограниченным параллелизмом; ошибка или зависание одного чата не должны задерживать запуск остальных.
- Выполнять не более пяти попыток с backoff 5 секунд, 30 секунд, 2 минуты и 5 минут; после лимита сохранять job как failed.
- Считать Telegram transport at-least-once: сбой после внешней отправки, но до локального marker может привести к дублю.
- Перед sender выполнять условный транзакционный gate active job и удерживать строку до terminal/retry update, чтобы административная cancellation, зафиксированная первой, исключала отправку.
- Отменённые jobs не удалять и не retry-ить; повторная обработка должна безопасно завершаться без sender.
- После transport-успеха фиксировать конкретную привязку в отдельном DI scope.
- HTTP adapter тестировать через fake `HttpMessageHandler`, не обращаясь к Telegram.
