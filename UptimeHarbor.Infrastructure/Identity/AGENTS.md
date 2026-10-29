# Identity и вход

`ApplicationUser`/`ApplicationRole` принадлежат Infrastructure. `IdentityUserAccountStore` реализует прикладной account store и bootstrap store; sign-in service изолирует cookie-сценарии.

Использовать общий scoped `UptimeHarborDbContext`. Bootstrap создаёт только отсутствующего администратора; блокировка обновляет security stamp. `IsEnabled`, Identity lockout и результат входа не смешивать; пароль и stamp не логировать.

Целевая роль только Administrator. `ApplicationUser.ApplySharedAccount` переносит общие credential-поля и UserRevision, сохраняя локальные lockout counters. Принятый аккаунт + роль + версия + cursor подготавливает `ProjectionRepository` в одном tracker и сохраняет projection UoW, без auto-commit `UserManager`. Общие account/hash/stamps/роль применяются через reducer/snapshot 04. Enabled использует AcceptedUserSignInService, AcceptedUserAccountStore и AcceptedAccountSession. Пароль подготавливает AccountCredentialService (Identity V3, 100000 iterations) до durable Pending; login не вызывает rehash/UserManager.Update. LocalLoginUnitOfWork сохраняет только локальные counters/lockout, общий credential проверяется повторно перед выдачей cookie. Локальный session epoch меняется при restart/recovery; cookie валидируется на каждом запросе, ключи Data Protection разделены NodeId и не реплицируются. Legacy store/bootstrap остаются только путём Disabled.

ClusterAccountBootstrapper готовит единственную точную команду для trusted admission и подаёт её только в Bootstrap новой сетки. Join/Restart не создают пользователей/роли. Production admission по умолчанию закрыт; сохранение prepared command, fencing и controlled enable принадлежат будущему lifecycle.
