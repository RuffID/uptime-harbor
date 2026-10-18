# Тесты Telegram adapters

Sender проверяется fake HTTP handler. Dispatcher тесты открывают SQLite in-memory и проверяют durable outbox, retry, lease и recovery cancellation.

При запрете БД запускать только sender tests. Не использовать реальный token и сеть; управляемые interleavings не считать доказательством SQL Server row locks.
