# Аутентификация и видимость

## Состояние реализации

Identity, cookie pipeline и адаптеры пользователей находятся в Infrastructure; application use case вычисляют доступ и управляют permissions; Web содержит страницы входа/выхода и защищённые MVC endpoints. Общая навигация показывает логин текущего пользователя перед POST-выходом. Административная страница `/Admin/Users` позволяет без перезагрузки создавать обычных пользователей, включать/блокировать, удалять и заменять видимость серверов и контейнеров. Контейнеры доступны в UI только при выбранном родительском сервере; текущий администратор не может отключить или удалить себя, а API не допускает отключение или удаление последнего включённого администратора.

## Identity-модель

Пользователь и роль используют `Guid` как ключ. `ApplicationUser` дополнительно хранит `IsEnabled` и изменяет его только методами `Enable`/`Disable`.

Identity-типы находятся в Infrastructure, потому что наследуют типы ASP.NET Core Identity. Domain хранит permissions только как независимые идентификаторы пользователя и объекта.

## Аутентификация

- Самостоятельная регистрация отсутствует.
- Начальный администратор создаётся из валидированных options только при отсутствии записи в роли Administrator.
- Повторный bootstrap не меняет существующий пароль.
- Весь пользовательский интерфейс требует аутентификации.
- Административные endpoints требуют роль `Administrator`.
- Заблокированный пользователь не должен продолжать получать доступ после завершения или инвалидации сессии.
- Пять последовательных неудачных проверок пароля временно блокируют Identity-вход на пять минут; успешный вход до порога сбрасывает счётчик.

`InitialAdministratorBootstrapper` идемпотентно создаёт роли `Administrator` и `User`, а администратора создаёт только при отсутствии пользователей в административной роли. Если существуют только disabled administrators, bootstrap включает одну существующую запись без создания дубликата и без смены пароля. `BootstrapAdminOptions` валидируются при фактической необходимости создать администратора; повторный запуск не меняет пароль существующего пользователя.

Web pipeline вызывает authentication до authorization. Cookie имеет `HttpOnly`, `SameSite=Lax` и `Secure=Always`. Security stamp проверяется на каждом запросе. Блокировка меняет `IsEnabled`, обновляет security stamp, а cookie validator немедленно отклоняет сессию заблокированного пользователя.

Страницы `/Account/Login` и `/Account/Logout` используют одинаковую ошибку для неизвестного логина, неверного пароля и заблокированной записи. Публичной регистрации и восстановления пароля нет. Все Razor Pages, кроме входа и страницы ошибки, требуют аутентификации.

`lockoutOnFailure` включён. Identity lockout не изменяет доменный/административный `IsEnabled`: disabled administrator не накапливает password failures, а bootstrap не снимает Identity lockout и не создаёт второго администратора. Для единственного администратора операционное восстановление после ошибочных попыток — дождаться окончания пятиминутной временной блокировки и войти с корректным паролем; автоматического сброса пароля или lockout при старте нет.

Login дополнительно ограничен десятью запросами в минуту на адрес соединения. Остальные API и SignalR не получают эту login-specific policy; ответ rate limiter не сообщает, существует ли логин.

Web security headers применяются middleware до authentication/authorization: сохраняются `nosniff`, `no-referrer` и `X-Frame-Options: DENY`, а CSP ограничивает scripts, connections, objects, base URI, framing и forms same-origin политикой без внешних origins.

## Видимость объектов

`ServerVisibilityPermission` связывает пользователя и сервер. `ContainerVisibilityPermission` хранит пользователя, сервер и контейнер.

Составные внешние ключи требуют, чтобы permission контейнера ссылался на permission того же пользователя к родительскому серверу и на контейнер этого сервера.

Application use case обязан фильтровать данные до формирования DTO. UI-скрытие не считается защитой. Те же правила применяются к controller endpoints и SignalR-событиям.

Административные use case управления пользователями, объектами и уведомлениями вызывают единый `IAdministratorAccessPolicy` до чтения и любого side effect. Policy не зависит от ASP.NET Core, Identity, EF Core и HTTP: неаутентифицированный пользователь получает semantic `Unauthorized`, disabled и обычный пользователь — согласованный `Forbidden`. Controller role metadata остаётся дополнительной Web-защитой, а не заменой Application-проверки.

Администратор получает полный список без отдельных permissions. Обычный пользователь не получает контейнеры только на основании доступа к серверу.

`ObjectVisibilityPolicy` остаётся чистой синхронной политикой и единообразно требует server permission для сервера и одновременно server + container permissions для контейнера. `DashboardQuery` получает materialized records через отдельный `IDashboardSnapshotReadQuery` и применяет policy до формирования dashboard DTO. `MonitoringEventRecipientResolver` получает отдельный materialized набор кандидатов через `IMonitoringRecipientReadQuery`, применяет ту же policy и возвращает только разрешённые server-side SignalR user-id; администраторы получают события всех объектов, disabled users исключаются.

Отдельного пользовательского `/api/objects` нет. Единственный v1 read-контракт обычного пользователя — защищённый dashboard snapshot без endpoint URL и административных полей; endpoint также отсутствует в промежуточных dashboard read records. Recipient query получает пользователей и признак Administrator одним materialized Identity query без per-user `IsInRoleAsync`, а server/container permissions обычных пользователей — пакетными materialized запросами без per-user N+1.

`UserManagementService` проверяет administrator access через общую policy внутри use case и поддерживает создание с начальным паролем, включение, блокировку, удаление и замену permissions. Self-disable/self-delete возвращают `Forbidden`, попытка убрать последнего enabled administrator — `Conflict`. Identity store повторяет last-admin guard внутри транзакции и изменяет concurrency stamp общей роли Administrator, чтобы две конкурентные операции над разными администраторами не могли оставить систему без enabled administrator. Контейнер проверяется вместе с родительским сервером до транзакционного изменения permissions. Удаление пользователя удаляет permissions каскадами БД.
