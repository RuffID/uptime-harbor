# Прикладные правила доступа

`IUserAccessContext` описывает текущего пользователя без ASP.NET Core; `IAdministratorAccessPolicy` возвращает семантический отказ до I/O. Единственная роль — Administrator. В cluster mode `AcceptedAdministratorAccessPolicy` читает `IAcceptedAccountReader` по UserId: claims активности/роли не заменяют принятое состояние.

Не читать claims и cookies здесь. Их адаптер находится в Web; проверка административного use case обязательна независимо от controller authorization.

Перед принятием команды и внешним test-send повторять проверку. Неизвестный/recovering аккаунт, deleted и disabled дают отказ. Общий sign-in и lifecycle сессий подключаются отдельно.
