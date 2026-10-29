# Identity и вход

`ApplicationUser`/`ApplicationRole` принадлежат Infrastructure. `IdentityUserAccountStore` реализует прикладной account store и bootstrap store; sign-in service изолирует cookie-сценарии.

Использовать общий scoped `UptimeHarborDbContext`. Bootstrap создаёт только отсутствующего администратора; блокировка обновляет security stamp. `IsEnabled`, Identity lockout и результат входа не смешивать; пароль и stamp не логировать.

Целевая роль только Administrator. `ApplicationUser.ApplySharedAccount` переносит общие credential-поля и UserRevision, сохраняя локальные lockout counters. Принятый аккаунт + роль + версия + cursor подготавливает `ProjectionRepository` в одном tracker и сохраняет projection UoW, без auto-commit `UserManager`. Общие account/hash/stamps/роль применяются через reducer/snapshot 04. Общий вход/сессии и пользовательское управление через журнал ещё не подключены (05.2–05.4); существующий store остаётся локальным путём Disabled.
