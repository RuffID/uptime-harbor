# Тесты persistence

- ProjectionTransactionTests включает monitoring incident/event/intents в один production write set и реальный UnitOfWork с fault до/после fake commit; replay не повторяет эффекты и не снимает latched fault. Это не доказательство SQL constraints/atomicity.

Model metadata и SQL Server conflict classifier проверяются изолированно; concurrency, materialized reads и unique constraints используют SQLite in-memory.

При запрете БД выбирать только metadata/classifier/fake/registration классы. `SharedStorageModelTests`, `SharedStorageMigrationTests` и `SharedStorageRegistrationTests` не открывают соединение; `ProjectionTransactionTests` проверяет фактический UoW с подставной сессией/репозиторием. Это не подтверждает SQL Server atomicity. SQLite не доказывает SQL Server 2601/2627, row-lock ordering и применение production migrations.
