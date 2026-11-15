# Тесты Identity

Legacy bootstrap проверяется fake store; прежний UserSignInServiceTests — реальным Identity поверх SQLite in-memory. Новые accepted account tests используют production orchestration с изолированными storage/authentication doubles, без SQL.

SQLite DI использует `SqliteTestDatabase.Configure`; правила совместимости тестовой модели находятся в [Persistence/AGENTS.md](../Persistence/AGENTS.md).

При запрете БД допустимы InitialAdministratorBootstrapperTests, AcceptedSignInTests, AcceptedCookieTests и AccountBootstrapTests: реальные credential/session/local-login orchestration и cookie callback с изолированными Identity/storage/authentication doubles, без provider. Эти классы выбирать явно. Sign-in тесты открывают SQLite connection и требуют разрешённого объёма проверки; SQL Server этим не подтверждается.
