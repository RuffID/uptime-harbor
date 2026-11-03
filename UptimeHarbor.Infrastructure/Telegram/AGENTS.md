# Infrastructure Telegram

TelegramOutboxWorker принадлежит единственному ManagedWorkerSupervisor, не отдельному hosted registration. Перед HTTP sender проверяет ambient RuntimeGeneration. Setup и несендеры не требуют token при startup; необязательный getMe для определения BotId первого узла не назначает sender и не разрешает доставку. Новый managed dispatch закрыт до этапа 05а.

## Ответственность

Каталог содержит `TelegramOptions`, HTTP transport Bot API, durable outbox dispatcher и отдельный hosted worker доставки.

## Правила

- Получать единственный token только через `IOptions<TelegramOptions>`.
- `HasValidBotToken` проверяет положительный числовой ID, двоеточие и допустимый секрет; transport повторно проверяет формат до HTTP. URI Bot API задавать относительным с `./`, чтобы двоеточие токена не стало URI scheme.
- Успех требует HTTP success и JSON `ok: true`. Ошибки классифицировать через `NotificationDeliveryFailure`; наружу передавать только контролируемые сообщения, без тела ответа и исходных transport exceptions.
- Не логировать request URI, token, response body и текст сообщения; стандартное HTTP-логирование клиента отключать.
- Telegram transport не вызывается из monitoring event publisher: monitoring атомарно сохраняет outbox job, а отдельный worker доставляет его позже.
- Получателей и задания обрабатывать с ограниченным параллелизмом; ошибка или зависание одного чата не должны задерживать запуск остальных.
- Выполнять не более пяти попыток с backoff 5 секунд, 30 секунд, 2 минуты и 5 минут; после лимита сохранять job как failed.
- Считать Telegram transport at-least-once: сбой после внешней отправки, но до локального marker может привести к дублю.
- Перед sender выполнять условный транзакционный gate active job и удерживать строку до terminal/retry update, чтобы административная cancellation, зафиксированная первой, исключала отправку.
- Отменённые jobs не удалять и не retry-ить; повторная обработка должна безопасно завершаться без sender.
- После transport-успеха фиксировать конкретную привязку в отдельном DI scope.
- HTTP adapter тестировать через fake `HttpMessageHandler`, не обращаясь к Telegram.
- В enabled-режиме legacy worker не регистрируется, ClusterNotificationSender запрещает прежние вызовы без принятого AttemptId. AcceptedDeliveryDispatcher использует SenderAuthorityGate для всех отправок; health getMe не даёт права отправки, ошибка отдельного чата не означает общий outage.
- Accepted transport требует положительный message_id, ограничивает response body 64 KiB и сохраняет только безопасную категорию/retry_after (1–86400 секунд). Открытый текст ошибок и transport exceptions не передаются в журнал или логи.
