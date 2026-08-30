# Web-приложение

## Composition root

`UptimeHarbor.Web/Program.cs` выполняет только настройку host, модульные DI-вызовы, middleware и endpoint mapping. Прикладная логика в нём не размещается.

Текущий pipeline:

1. production exception handler и HSTS вне Development;
2. обработка `X-Forwarded-*` только от явно настроенной proxy-сети с `ForwardLimit = 1`;
3. HTTPS redirection;
3. routing;
4. authentication;
5. authorization;
6. static assets;
7. MVC controllers;
8. Razor Pages;
9. SignalR hub;
10. health endpoints `/health/live` (liveness) и `/health/ready` (PostgreSQL readiness); `/health` оставлен алиасом readiness.

До построения pipeline вызывается инфраструктурный bootstrap ролей и начального администратора. Миграции при этом не применяются автоматически.

## Типы endpoint

- Пользовательские страницы реализуются Razor Pages.
- HTTP API должно реализовываться MVC-контроллерами с attribute routing.
- Minimal API handlers `MapGet`, `MapPost`, `MapPut`, `MapDelete` и `MapPatch` не используются.
- SignalR hub сопоставлен по пути `/hubs/monitoring`.
- Стандартный health-check endpoint сопоставлен по пути `/health`.
- API чтения доступных объектов сопоставлено под `/api/objects`.
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

Административная страница объектов находится в `/Admin/Objects`. PageModel не загружает коллекции: Razor содержит только статические контейнеры и формы, а данные загружает TypeScript-модуль через MVC API. DTO содержит числовой `version`; update/delete возвращает его обратно как expected version. Stale request получает envelope `{ success: false, message }` и HTTP `409 Conflict`, после чего UI может перечитать актуальный список. Интерфейс работает без перезагрузки, создаёт динамические списки через DOM API без `innerHTML`, показывает серверные ошибки рядом с полями и требует подтверждение удаления. TypeScript собирается строгим `Microsoft.TypeScript.MSBuild` из `wwwroot/ts` в `wwwroot/dist`; target `PrepareTypeScriptForStaticWebAssets` перед `ResolveProjectStaticWebAssets` и `ResolveCoreStaticWebAssets` выполняет поиск конфигурации, удаление outputs другой конфигурации и `CompileTypeScriptWithTSConfig`. Поэтому удаление старых outputs всегда предшествует компиляции, а оба этапа разрешения static assets выполняются только после восстановления generated-файлов. Инкрементальный режим отключён, чтобы удалённые generated-файлы всегда восстанавливались.

Административная страница уведомлений находится в `/Admin/Notifications` и следует тому же AJAX-only контракту. Она управляет чатами, тестовой отправкой и независимыми server/container bindings; bot token отсутствует в HTML, TypeScript и API payload. Состояния loading, empty, error и saving управляются клиентским модулем, опасные удаления требуют подтверждения.

Административная страница пользователей находится в `/Admin/Users`. TypeScript загружает пользователей, серверы и контейнеры через MVC API, строит permissions через DOM API без `innerHTML`, перехватывает submit и передаёт antiforgery для mutating requests. Формы содержат связанные `id`/`name` и выводят field-specific ошибки.

Главная страница `/` содержит статические контейнеры панели и не загружает коллекции в PageModel. TypeScript получает snapshot через `/api/dashboard`, выполняет server-side поиск по текущей строке, строит вложенные карточки DOM API и показывает отдельные состояния loading, empty, no-results и error. Тёмный layout использует Bootstrap и адаптивную сетку для desktop/mobile. Браузерный SignalR client фиксированной версии подключён как внешний статический script; потеря push-канала не блокирует HTTP snapshot.

`JsonResultMapper` возвращает единый envelope и при field-specific validation добавляет необязательное поле `field`. Остальные ошибки сохраняют контракт `{ success: false, message: ... }` без дополнительного поля.

## SignalR

`MonitoringHub` требует аутентификацию и не содержит вызываемых клиентом методов выбора пользователя или группы. Стандартный SignalR user-id берётся из аутентифицированного `NameIdentifier`. `SignalRMonitoringEventHandler` получает разрешённые user-id через Application resolver и не делает broadcast данных объекта.

Событие `MonitoringObjectChanged` содержит только `objectId` и `objectKind`; получатель перечитывает защищённый snapshot. Клиент включает автоматическое переподключение, повторяет initial start после исчерпания reconnect-попыток и после `onreconnected` полностью заменяет состояние актуальным snapshot.

После успешного административного сохранения `SignalRObjectAdministrationChangePublisher` отправляет всем аутентифицированным открытым панелям только событие `ObjectAdministrationChanged` без данных объекта. Получатель должен перечитать разрешённый ему snapshot через защищённый API; событие не является источником истины и не раскрывает скрытые объекты.
