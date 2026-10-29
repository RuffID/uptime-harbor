# Аутентификация и видимость

## Внутренняя авторизация monitoring (06)

RecordObservations требует admitted NodeId/storage generation, совпадающих с аутентифицированным peer envelope. EvaluateAvailability/PlanBacklogDelivery допускаются только текущим локальным leader с настоящим term/leadership/catch-up proof. Эти команды имеют ActorUserId=Empty/ActorRevision=0 и закрытый SystemSource; пользовательская команда не может использовать monitoring payload или system source.

Изменение целей/observers/NodeChatRoute остаётся обычной принятой Administrator-командой. Service facts не используют пользователя, его cookie или фиктивную роль. Аудит переходов содержит system source и безопасные IDs после apply, без secrets/actor fabrication; account/session/recovery-version инварианты 05 сохранены.

## Состояние реализации

Identity, cookie pipeline и адаптеры пользователей находятся в Infrastructure; Web содержит прежние страницы входа/выхода и административные endpoints. Этапы 02/04 подготовили общие accounts/storage/reducer/snapshot, а ранний 05.1 — accepted account reader и Administrator policy. 05.2–05.4 подключили принятый sign-in, локальные сессии/lockout, безопасные команды пользователей и аккаунтный bootstrap/recovery. Единственная сохраняемая роль — Administrator; legacy создание аккаунта назначает эту роль. Старый UI permissions ещё не переработан; рабочие SetVisibility API/request/use case и ACL repository/UoW удалены.

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

Прежний `InitialAdministratorBootstrapper` теперь создаёт только Administrator. Его остальные однопроцессные правила пока сохранены: создание при отсутствии аккаунта, включение существующего disabled administrator без смены пароля, options только при необходимости. В enabled-режимах этот bootstrap не вызывается. `ClusterAccountBootstrapper` принимает первый аккаунт только в новой сетке по точному digest подготовленной команды из trusted RecoveryAdmission; Join/Restart не создают аккаунтов или ролей. Сохранение prepared command, внешнее fencing и controlled enable остаются этапом 09, admission по умолчанию закрыт.

Web pipeline вызывает authentication до authorization. Cookie имеет `HttpOnly`, `SameSite=Lax` и `Secure=Always`. Security stamp проверяется на каждом запросе. Блокировка меняет `IsEnabled`, обновляет security stamp, а cookie validator немедленно отклоняет сессию заблокированного пользователя.

Страницы `/Account/Login` и `/Account/Logout` используют одинаковую ошибку для неизвестного логина, неверного пароля и заблокированной записи. Публичной регистрации и восстановления пароля нет. Все Razor Pages, кроме входа и страницы ошибки, требуют аутентификации.

`lockoutOnFailure` включён. Identity lockout не изменяет доменный/административный `IsEnabled`: disabled administrator не накапливает password failures, а bootstrap не снимает Identity lockout и не создаёт второго администратора. Для единственного администратора операционное восстановление после ошибочных попыток — дождаться окончания пятиминутной временной блокировки и войти с корректным паролем; автоматического сброса пароля или lockout при старте нет.

Login дополнительно ограничен десятью запросами в минуту на адрес соединения. Остальные API и SignalR не получают эту login-specific policy; ответ rate limiter не сообщает, существует ли логин.

Web security headers применяются middleware до authentication/authorization: сохраняются `nosniff`, `no-referrer` и `X-Frame-Options: DENY`, а CSP ограничивает scripts, connections, objects, base URI, framing и forms same-origin политикой без внешних origins.

## Видимость объектов

Административные use case по-прежнему вызывают `IAdministratorAccessPolicy` до чтения и side effects. Controller role metadata — дополнительная Web-защита. Неаутентифицированный получает Unauthorized, отключённый или неадминистратор — Forbidden. Активные Administrator имеют полный доступ.

`DashboardQuery` проверяет единую administrator policy до чтения, поиска и counts, затем возвращает все server/container records, включая контейнеры без родителя; ресурсной фильтрации нет. Endpoint URL в dashboard records/DTO не входит. В Disabled `MonitoringRecipientReadQuery` получает users/Administrator одним запросом; enabled `AcceptedMonitoringRecipients` читает принятых активных Administrator из immutable image. Publishers повторно проверяют локальные сессии и адресуют только разрешённые connection IDs.

`ObjectVisibilityPolicy` предоставляет доступ только Administrator, независимо от старых permission-наборов или родителя контейнера. ACL repositories/UoW, SetVisibility request/use case и controller endpoint удалены. DTO-поля назначений возвращают пустые наборы; старый UI остаётся до будущей переработки. Отдельных ролей, владельцев и операторов нет.

`UserManagementService` сохраняет существующие self-disable/self-delete и last-enabled-admin проверки. Legacy Identity store повторяет last-admin guard в локальной транзакции с concurrency stamp роли. Это не доказательство упорядоченного согласования аккаунтов нескольких узлов. `AcceptedAdministratorAccessPolicy` в enabled-режиме получает `IAcceptedAccountReader.Find(UserId)` из принятого образа. Анонимный, unknown/unaccepted, disabled/deleted и неадминистратор получают отказ; claims роли/активности не заменяют accepted state. Во время replay чтение закрыто до подтверждения догона. Командный reducer повторяет авторизацию и actor revision, membership — после drain, test-send — непосредственно перед sender после чтения чата. В enabled-режиме `AcceptedUserSignInService` читает только принятые login/hash/stamps и роль; `LocalAccountLogin` сохраняет только локальные counters/lockout через узкий UoW. Pending пароль не используется, успешный вход не пишет общий журнал и не делает локальный rehash. `AcceptedAccountSession` связывает cookie с credential и локальным process epoch: restart, untrusted recovery, latched fault и apply смены hash/stamp/disable/delete отзывают прежнюю сессию. `MonitoringConnections` прекращает соответствующие SignalR-подписки. Обычный offline использует последнее достоверное принятое состояние; enabled browser gate пока закрыт до будущего controlled enable. Аккаунты используют общий log/snapshot, отдельного Identity replication нет.

## Согласованные изменения пользователей

`SharedUserManagementService` принимает Create, Update (login), Password, SetEnabled и Delete с явными общими UserId, OperationId и base revision. Только роль Administrator назначается из принятого образа. Проверки active actor, уникального нормализованного login (включая резерв tombstone), self-disable/self-delete и последнего активного администратора выполняются при подаче и повторяются reducer в порядке журнала. Отложенная команда отключённого или изменившего ревизию актора отвергается.

`AccountCredentialService` использует Identity V3 PBKDF2/SHA512, 100000 iterations и password validators. Открытый пароль существует только на входе; durable Pending защищена локальной Data Protection. Retry сверяет исходный prepared hash, действие и ID, не создавая новую соль/временную метку. После потери ответа возвращается исходный accepted result; удержанная restore-заявка не подаётся молча. Пользователь получает только ID/status/revision/conflict, без вариантов credentials. Аудит различает LocalPending и подтверждённый terminal outcome с operation/actor/subject/node IDs; Resolved не пишет повторный успех.

Общие аккаунты не принадлежат узлу создания. Snapshot/replay восстанавливают UserId/RoleId, credentials и tombstones; сессии, Data Protection keys и counters не копируются. Local process epoch дополнительно делает прежние cookies недействительными после restart/restore. Реальный ввод новой сетки и browser acceptance остаются отдельно от этих компонентов.
