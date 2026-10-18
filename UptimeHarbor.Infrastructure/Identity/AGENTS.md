# Identity и вход

`ApplicationUser`/`ApplicationRole` принадлежат Infrastructure. `IdentityUserAccountStore` реализует прикладной account store и bootstrap store; sign-in service изолирует cookie-сценарии.

Использовать общий scoped `UptimeHarborDbContext`. Bootstrap создаёт только отсутствующего администратора; блокировка обновляет security stamp. `IsEnabled`, Identity lockout и результат входа не смешивать; пароль и stamp не логировать.
