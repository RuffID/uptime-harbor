# UptimeHarbor.Web

## Ответственность

Исполняемый ASP.NET Core-проект содержит composition root, Razor Pages, MVC-контроллеры, SignalR, Web-конфигурацию и статические ресурсы.

## Правила

- `Program.cs` содержит только builder, вызовы модульных DI-расширений, middleware и endpoint mapping.
- HTTP API реализовывать контроллерами; не добавлять `MapGet`, `MapPost` и другие Minimal API handlers.
- Razor Pages использовать для страниц, контроллеры — для HTTP API, SignalR hub — для push-обновлений.
- Не обращаться к EF Core или Telegram API напрямую из UI.
- Изменяющие запросы защищать авторизацией и antiforgery согласно типу клиента.
- При изменении Web pipeline, endpoint или UI-границ обновлять этот файл и `Documentation/Technical/Web application.md`.
- Все Razor Pages, кроме явно разрешённых страниц входа и ошибки, требуют аутентификации; административное API требует роль `Administrator`.
- `ServiceResult` преобразовывать в единый JSON envelope через Web-мэппер, не помещая HTTP-статусы в Application.
- Административное API объектов размещено под `/api/admin/objects`; после сохранения публикуется только обезличенный сигнал `ObjectAdministrationChanged`.
- Administrative object DTO/request передают числовой `Version`; stale update/delete возвращает единый JSON envelope с HTTP `409 Conflict`.
- Новый клиентский код писать на TypeScript в `wwwroot/ts`, собранный JavaScript подключать из `wwwroot/dist`.
- Полная TypeScript-цепочка `FindConfigFiles` → `TypeScriptDeleteOutputFromOtherConfigs` → `CompileTypeScriptWithTSConfig` должна выполняться через `PrepareTypeScriptForStaticWebAssets` перед `ResolveProjectStaticWebAssets` и `ResolveCoreStaticWebAssets`, чтобы удаление outputs другой конфигурации не происходило после компиляции и сборка решения сначала восстанавливала `wwwroot/dist`.
- Инкрементальная TypeScript-сборка отключена, чтобы отсутствующие generated `.js` восстанавливались и в Debug, и в Release.
- API главной панели размещено под `/api/dashboard`, требует аутентификацию и возвращает только отфильтрованные dashboard DTO без endpoint URL через `JsonResultMapper`; отдельного публичного `/api/objects` нет.
- Monitoring SignalR-событие содержит только тип и идентификатор объекта и отправляется сервером конкретным разрешённым user-id; клиент перечитывает snapshot после события и reconnect.
- Клиент объединяет SignalR refresh-события окном 200 мс и отменяет устаревший snapshot-fetch через `AbortController`; reconnect загружает snapshot немедленно.
- Автоматические `[ApiController]` model-state ошибки используют тот же `{ success: false, message, field? }` envelope, что и `JsonResultMapper`.
- Login имеет отдельную rate-limit policy; Web добавляет `nosniff`, `no-referrer`, запрет framing и применяемую CSP. SignalR browser client поставляется локально в `wwwroot/vendor/signalr/signalr.min.js` (версия 10.0.11, SHA-256 зафиксирован в технической документации).
- Административное API уведомлений размещено под `/api/admin/notifications`; token не входит в Web-контракты, а mutating actions и тестовая отправка требуют роль `Administrator` и antiforgery.
- Административная страница пользователей размещена под `/Admin/Users`; UI работает через `/api/admin/users` без перезагрузки, а операции изменения защищены antiforgery и ролью `Administrator`.
- Forwarded headers принимаются только из явно настроенной proxy-сети с ограничением `ForwardLimit` до одного прокси; liveness и readiness health endpoints разделены.
