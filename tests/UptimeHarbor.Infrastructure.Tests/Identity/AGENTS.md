# Тесты Identity

Bootstrap проверяется fake store; sign-in/lockout — реальным Identity поверх SQLite in-memory.

При запрете БД допустим только fake bootstrap-набор. Sign-in тесты открывают SQLite connection и требуют разрешённого объёма проверки; SQL Server этим не подтверждается.
