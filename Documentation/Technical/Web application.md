# Web-приложение

## Composition root

`UptimeHarbor.Web/Program.cs` выполняет только настройку host, модульные DI-вызовы, middleware и endpoint mapping. Прикладная логика в нём не размещается.

Текущий pipeline:

1. production exception handler и HSTS вне Development;
2. обработка `X-Forwarded-*` только от явно настроенной proxy-сети с `ForwardLimit = 1`;
3. routing;
4. security headers/CSP и login rate limiting;
5. authentication;
6. authorization;
7. static assets;
8. MVC controllers;
9. Razor Pages;
10. SignalR hub;
11. health endpoints `/health/live` (liveness) и `/health/ready` (SQL Server readiness); `/health` оставлен алиасом readiness.

TLS завершается на Nginx. Web всегда принимает внутренний HTTP и не вызывает `UseHttpsRedirection`; внешний HTTP → HTTPS redirect и передача доверенного `X-Forwarded-Proto: https` принадлежат Nginx.

До построения pipeline `InitializeInfrastructureAsync` проверяет подключение к SQL Server, при наличии pending migrations создаёт backup и применяет их, после чего выполняет bootstrap ролей и начального администратора. Ошибка или 60-секундный timeout database startup останавливают запуск до построения HTTP pipeline.

## Типы endpoint

- Пользовательские страницы реализуются Razor Pages.
- HTTP API должно реализовываться MVC-контроллерами с attribute routing.
- Minimal API handlers `MapGet`, `MapPost`, `MapPut`, `MapDelete` и `MapPatch` не используются.
- SignalR hub сопоставлен по пути `/hubs/monitoring`.
- Стандартный health-check endpoint сопоставлен по пути `/health`.
- API snapshot главной панели сопоставлено под `/api/dashboard` и требует аутентификацию.
- API управления пользователями сопоставлено под `/api/admin/users` и требует роль `Administrator`.
- API управления серверами и контейнерами сопоставлено под `/api/admin/objects`, требует роль `Administrator`, а все изменяющие методы защищены antiforgery.
- API управления Telegram-чатами и привязками сопоставлено под `/api/admin/notifications`, требует роль `Administrator`, а все изменяющие методы и тестовая отправка защищены antiforgery.

`MapHealthChecks` и `MapHub` являются специализированными mappings платформы, а не обработчиками бизнес-API.

## Presentation DI

`AddWebPresentation` регистрирует controllers, Razor Pages, SignalR и health checks. Новые presentation-зависимости должны добавляться через этот или более узкий extension-метод, а не прямыми lifetime-регистрациями в `Program.cs`.

## Razor Pages

Страницы входа и выхода находятся в `/Account`. Для всего каталога Razor Pages действует обязательная авторизация; анонимный доступ разрешён только странице входа и странице ошибки.

Изменяющие формы должны использовать POST, antiforgery и серверную авторизацию. API-контроллеры должны возвращать явные HTTP-контракты и также проверять роль/доступ.

Административная страница объектов находится в `/Admin/Objects`. PageModel не загружает коллекции: Razor содержит только статические контейнеры и формы, а данные загружает TypeScript-модуль через MVC API. DTO содержит числовой `version`; update/delete возвращает его обратно как expected version. Stale request получает envelope `{ success: false, message }` и HTTP `409 Conflict`, после чего UI может перечитать актуальный список. Интерфейс работает без перезагрузки, создаёт динамические списки через DOM API без `innerHTML`, показывает серверные ошибки рядом с полями и требует подтверждение удаления. TypeScript собирается строгим `Microsoft.TypeScript.MSBuild` 7.0.1 из `wwwroot/ts` в `wwwroot/dist`; target `PrepareTypeScriptForStaticWebAssets` выполняет новые package targets `SetTscExecutable` и `RunTscCompile` перед `ResolveProjectStaticWebAssets` и `ResolveCoreStaticWebAssets`. Поэтому актуальные outputs восстанавливаются до разрешения static assets. `CleanTypeScriptOutputs` в эту цепочку не входит, поскольку Static Web Assets фиксирует items до выполнения targets. MSBuild сохраняет target `ES2022` и не генерирует source maps, как и `tsconfig.json`. Удалённые в TypeScript 7 targets старой MSBuild-цепочки не используются.

Административная страница уведомлений находится в `/Admin/Notifications` и следует тому же AJAX-only контракту. Она управляет чатами, тестовой отправкой и независимыми server/container bindings; bot token отсутствует в HTML, TypeScript и API payload. Состояния loading, empty, error и saving управляются клиентским модулем, опасные удаления требуют подтверждения.

Административная страница пользователей находится в `/Admin/Users`. TypeScript загружает пользователей, серверы и контейнеры через MVC API, строит permissions через DOM API без `innerHTML`, перехватывает submit и передаёт antiforgery для mutating requests. Формы содержат связанные `id`/`name` и выводят field-specific ошибки.

Главная страница `/` содержит статические контейнеры панели и не загружает коллекции в PageModel. TypeScript получает snapshot через `/api/dashboard`, выполняет server-side поиск по текущей строке, строит вложенные карточки DOM API и показывает отдельные состояния loading, empty, no-results и error. Пачка SignalR refresh-событий объединяется окном 200 мс. Каждый новый snapshot-fetch отменяет прежний через `AbortController`; штатный `AbortError` не отображается пользователю. Reconnect отменяет отложенный refresh и немедленно загружает актуальный snapshot. Тёмный layout использует Bootstrap и адаптивную сетку для desktop/mobile.

Публичный `/api/objects` удалён как неиспользуемый v1 endpoint, раскрывавший endpoint URL. Пользовательские read-контракты dashboard не содержат endpoint и административные поля; `/api/admin/objects` сохраняет endpoint URL для редактирования.

`JsonResultMapper` возвращает единый envelope и при field-specific validation добавляет необязательное поле `field`. Остальные ошибки сохраняют контракт `{ success: false, message: ... }` без дополнительного поля.

`InvalidModelStateResponseFactory` переводит автоматические `[ApiController]` ошибки в тот же envelope с HTTP 400. Ошибки body получают безопасное сообщение без `field`, ошибки конкретного поля — детерминированное camelCase-поле; exception message JSON parser и CLR-типы в ответ не включаются. TypeScript HTTP helper сохраняет нативный `AbortError`, возвращает `ApiError` с HTTP status для JSON, пустых и non-JSON error responses и по-прежнему fail-fast отклоняет неожиданный пустой успешный ответ.

Login Page имеет отдельную fixed-window policy: не более 10 попыток в минуту на `RemoteIpAddress`, без очереди. `X-Forwarded-For` влияет на этот адрес только после существующей проверки trusted proxy network. Ответ 429 одинаков для любого логина. Dashboard, admin API, SignalR и остальные страницы login-specific policy не получают.

На все ответы добавляются `X-Content-Type-Options: nosniff`, `Referrer-Policy: no-referrer`, `X-Frame-Options: DENY` и применяемая политика `Content-Security-Policy`: `default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self'; font-src 'self'; connect-src 'self'; object-src 'none'; base-uri 'self'; frame-ancestors 'none'; form-action 'self';`. Политика разрешает только локальные scripts/styles/images/fonts/forms и same-origin SignalR transport, запрещает object/embed и framing, не содержит `unsafe-inline`, `unsafe-eval`, wildcard или CDN origins.

Главная страница подключает локальный `UptimeHarbor.Web/wwwroot/vendor/signalr/signalr.min.js` с `asp-append-version="true"`. Asset предоставлен локально по `D:\Media\User\Downloads\signalr.min.js`, содержит версию `10.0.11`, скопирован без изменения; SHA-256: `97E9B97E642A72E5A470917147A2BF79F86CAD829A5C6786ADB10614D248BB95`. Дополнительный license/notice-файл вместе с предоставленным asset не поставлялся.

## SignalR

`MonitoringHub` требует аутентификацию и не содержит вызываемых клиентом методов выбора пользователя или группы. Стандартный SignalR user-id берётся из аутентифицированного `NameIdentifier`. `SignalRMonitoringEventHandler` получает разрешённые user-id через Application resolver и не делает broadcast данных объекта.

Событие `MonitoringObjectChanged` содержит только `objectId` и `objectKind`; получатель перечитывает защищённый snapshot. Клиент включает автоматическое переподключение, повторяет initial start после исчерпания reconnect-попыток и после `onreconnected` полностью заменяет состояние актуальным snapshot.

После успешного административного сохранения `SignalRObjectAdministrationChangePublisher` отправляет всем аутентифицированным открытым панелям только событие `ObjectAdministrationChanged` без данных объекта. Получатель должен перечитать разрешённый ему snapshot через защищённый API; событие не является источником истины и не раскрывает скрытые объекты.

Административный SignalR refresh является best-effort: publisher вызывается только после commit, изолирует и журналирует transport exception без данных объекта. Ошибка push не меняет успешный `ServiceResult` сохранённой операции и не требует повторного HTTP-запроса; после save/concurrency error publisher не вызывается.
