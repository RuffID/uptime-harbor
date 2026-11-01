# Тесты Telegram adapters

Sender проверяется fake HTTP handler. Dispatcher тесты открывают SQLite in-memory и проверяют durable outbox, retry, lease и recovery cancellation.

При запрете БД запускать только sender tests. Не использовать реальный token и сеть; управляемые interleavings не считать доказательством SQL Server row locks.

Sender tests используют синтаксически корректный вымышленный token и проверяют итоговый абсолютный Bot API URL, HTTP/JSON `ok`, безопасную классификацию отказов и отклонение заглушки до вызова handler.

Accepted transport дополнительно требует настоящий числовой message_id и сохраняет retry_after без response text. Отсутствующий ID не подменять синтетическим success.
