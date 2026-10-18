# Тесты persistence

Model metadata и SQL Server conflict classifier проверяются изолированно; concurrency, materialized reads и unique constraints используют SQLite in-memory.

При запрете БД выбирать только metadata/classifier классы. SQLite не доказывает SQL Server 2601/2627, row-lock ordering и применение production migrations.
