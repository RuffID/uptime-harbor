# Аутентификация и видимость

## Состояние реализации

Identity, cookie pipeline и адаптеры пользователей находятся в Infrastructure; Web содержит прежние страницы входа/выхода и административные endpoints. Этап 02 подготовил модель общих аккаунтов и storage mapping, но не реализовал общий вход или управление пользователями через журнал. Единственная сохраняемая роль — Administrator; legacy создание аккаунта назначает эту роль. Старый UI permissions ещё не переработан и больше не имеет соответствующего storage: попытка создать назначение отклоняется.

## Identity-модель

Пользователь и роль используют `Guid` как ключ. `ApplicationUser` хранит `IsEnabled`, `UserRevision`, `PasswordHashFormatVersion`, `DeletedAtUtc`. `ApplySharedAccount` переносит общий login/hash/stamps/активность, сохраняя локальные lockout counters. Новый projection UoW сохраняет account + Administrator membership + cursor одной транзакцией без UserManager autocommit.

Identity-типы остаются в Infrastructure. Чистый Domain `SharedAccount` задаёт FormKC/Trim/uppercase login и self/last-admin инвариант по принятому count. SQL normalized login обязателен и уникален; роль ограничена Administrator. Прежние permission-типы сохранены только для компиляции legacy use case, EF их не хранит. Детали и пределы — [Shared cluster storage.md](Shared%20cluster%20storage.md).

## Аутентификация

- Самостоятельная регистрация отсутствует.
- Начальный администратор создаётся из валидированных options только при отсутствии записи в роли Administrator.
- Повторный bootstrap не меняет существующий пароль.
- Весь пользовательский интерфейс требует аутентификации.
- Административные endpoints требуют роль `Administrator`.
- Заблокированный пользователь не должен продолжать получать доступ после завершения или инвалидации сессии.
- Пять последовательных неудачных проверок пароля временно блокируют Identity-вход на пять минут; успешный вход до порога сбрасывает счётчик.

Прежний `InitialAdministratorBootstrapper` теперь создаёт только Administrator. Его остальные однопроцессные правила пока сохранены: создание при отсутствии аккаунта, включение существующего disabled administrator без смены пароля, options только при необходимости. Этот bootstrap ещё не является cluster bootstrap/join и не должен использоваться для включения новой общей модели до реализации этапов 03–05/09.

Web pipeline вызывает authentication до authorization. Cookie имеет `HttpOnly`, `SameSite=Lax` и `Secure=Always`. Security stamp проверяется на каждом запросе. Блокировка меняет `IsEnabled`, обновляет security stamp, а cookie validator немедленно отклоняет сессию заблокированного пользователя.

Страницы `/Account/Login` и `/Account/Logout` используют одинаковую ошибку для неизвестного логина, неверного пароля и заблокированной записи. Публичной регистрации и восстановления пароля нет. Все Razor Pages, кроме входа и страницы ошибки, требуют аутентификации.

`lockoutOnFailure` включён. Identity lockout не изменяет доменный/административный `IsEnabled`: disabled administrator не накапливает password failures, а bootstrap не снимает Identity lockout и не создаёт второго администратора. Для единственного администратора операционное восстановление после ошибочных попыток — дождаться окончания пятиминутной временной блокировки и войти с корректным паролем; автоматического сброса пароля или lockout при старте нет.

Login дополнительно ограничен десятью запросами в минуту на адрес соединения. Остальные API и SignalR не получают эту login-specific policy; ответ rate limiter не сообщает, существует ли логин.

Web security headers применяются middleware до authentication/authorization: сохраняются `nosniff`, `no-referrer` и `X-Frame-Options: DENY`, а CSP ограничивает scripts, connections, objects, base URI, framing и forms same-origin политикой без внешних origins.

## Видимость объектов

Административные use case по-прежнему вызывают `IAdministratorAccessPolicy` до чтения и side effects. Controller role metadata — дополнительная Web-защита. Неаутентифицированный получает Unauthorized, отключённый или неадминистратор — Forbidden. Активные Administrator имеют полный доступ.

`DashboardSnapshotReadQuery` возвращает администратору все server/container records, включая контейнеры без родителя; неадминистративный compatibility-вызов получает пустые наборы. Endpoint URL в dashboard records/DTO не входит. `MonitoringRecipientReadQuery` получает users/Administrator одним запросом и передаёт пустые permission-наборы; resolver исключает disabled users.

Прежние `ObjectVisibilityPolicy`, DTO, use case и UI ресурсных назначений остаются до этапа 05, но соответствующих EF-таблиц нет. Compatibility repository читает пустые назначения и отвергает новые, cleanup не обращается к SQL. Это временная согласованность схемы и вызывающего кода, а не завершённый пользовательский сценарий этапа 05.

`UserManagementService` сохраняет существующие self-disable/self-delete и last-enabled-admin проверки. Legacy Identity store повторяет last-admin guard в локальной транзакции с concurrency stamp роли. Это не доказательство упорядоченного согласования аккаунтов нескольких узлов. Общая проверка принятого активного администратора, sign-in и синхронизация аккаунтов ещё не подключены.
