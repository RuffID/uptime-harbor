# Тесты Infrastructure

Подкаталоги разделяют Identity, monitoring, persistence и Telegram. Часть тестов использует SQLite in-memory и исполняет SQL; это не только metadata/fake проверки.

При запрете БД не запускать весь проект: выбирать точные классы без SQLite. Runtime SQL Server, реальные backup/migrations и Telegram не запускаются из изолированного набора.
