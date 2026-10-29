# Аутентификация и видимость

## Состояние реализации

Identity, cookie pipeline и адаптеры пользователей находятся в Infrastructure; Web содержит прежние страницы входа/выхода и административные endpoints. Этапы 02/04 подготовили общие accounts/storage/reducer/snapshot, а ранний 05.1 — accepted account reader и Administrator policy. Общий вход и пользовательское управление аккаунтами через журнал ещё не подключены. Единственная сохраняемая роль — Administrator; legacy создание аккаунта назначает эту роль. Старый UI permissions ещё не переработан; рабочие SetVisibility API/request/use case и ACL repository/UoW удалены.

## Identity-модель

Пользователь и роль используют `Guid` как ключ. `ApplicationUser` хранит `IsEnabled`, `UserRevision`, `PasswordHashFormatVersion`, `DeletedAtUtc`. `ApplySharedAccount` переносит общий login/hash/stamps/активность, сохраняя локальные lockout counters. Новый projection UoW сохраняет account + Administrator membership + cursor одной транзакцией без UserManager autocommit.

Identity-типы остаются в Infrastructure. Чистый Domain `SharedAccount` задаёт FormKC/Trim/uppercase login и self/last-admin инвариант по принятому count. SQL normalized login обязателен и уникален; роль ограничена Administrator. Прежние Domain permission-типы и пустые DTO-поля сохранены для совместимости, EF их не хранит; права доступа ими не предоставляются. Детали и пределы — [Shared cluster storage.md](Shared%20cluster%20storage.md).

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

`ObjectVisibilityPolicy` предоставляет доступ только Administrator, независимо от старых permission-наборов или родителя контейнера. ACL repositories/UoW, SetVisibility request/use case и controller endpoint удалены. DTO-поля назначений возвращают пустые наборы; старый UI остаётся до будущей переработки. Отдельных ролей, владельцев и операторов нет.

`UserManagementService` сохраняет существующие self-disable/self-delete и last-enabled-admin проверки. Legacy Identity store повторяет last-admin guard в локальной транзакции с concurrency stamp роли. Это не доказательство упорядоченного согласования аккаунтов нескольких узлов. `AcceptedAdministratorAccessPolicy` в enabled-режиме получает `IAcceptedAccountReader.Find(UserId)` из принятого образа. Анонимный, unknown/unaccepted, disabled/deleted и неадминистратор получают отказ; claims роли/активности не заменяют accepted state. Во время replay чтение закрыто до подтверждения догона. Командный reducer повторяет авторизацию и actor revision, membership — после drain, test-send — непосредственно перед sender после чтения чата. Общий sign-in, security-stamp/session lifecycle новой модели и пользовательские flows остаются в 05.2–05.4; enabled browser gate закрыт. Аккаунты используют общий log/snapshot, отдельного Identity replication нет.
