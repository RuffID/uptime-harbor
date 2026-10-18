# Прикладные правила доступа

`IUserAccessContext` описывает текущего пользователя без ASP.NET Core; `IAdministratorAccessPolicy` возвращает семантический отказ до I/O. `ApplicationRoles` содержит имена ролей.

Не читать claims и cookies здесь. Их адаптер находится в Web; проверка административного use case обязательна независимо от controller authorization.
