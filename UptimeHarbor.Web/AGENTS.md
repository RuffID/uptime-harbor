# UptimeHarbor.Web

## Ответственность

Исполняемый ASP.NET Core-проект содержит composition root, Razor Pages, MVC-контроллеры, SignalR, Web-конфигурацию и статические ресурсы.

## Правила

- `Program.cs` содержит только builder, вызовы модульных DI-расширений, middleware и endpoint mapping.
- `Program.cs` после создания builder загружает обязательный внешний `Config/config.json`, настраивает Serilog через host и только затем вызывает модульные DI-расширения.
- `appsettings.json` содержит только Serilog; operational/security settings находятся в неотслеживаемом и не публикуемом `Config/config.json`, а безопасная полная структура — в `Config/config.example.json`.
- HTTP API реализовывать контроллерами; не добавлять `MapGet`, `MapPost` и другие Minimal API handlers.
- Razor Pages использовать для страниц, контроллеры — для HTTP API, SignalR hub — для push-обновлений.
- Не обращаться к EF Core или Telegram API напрямую из UI.
- Изменяющие запросы защищать авторизацией и antiforgery согласно типу клиента.
- При изменении Web pipeline, endpoint или UI-границ обновлять этот файл и `Documentation/Technical documentation/Web application.md`.
- Все Razor Pages, кроме явно разрешённых страниц входа и ошибки, требуют аутентификации; административное API требует роль `Administrator`.
- `ServiceResult` преобразовывать в единый JSON envelope через Web-мэппер, не помещая HTTP-статусы в Application.
- Административное API объектов размещено под `/api/admin/objects`; после сохранения публикуется только обезличенный сигнал `ObjectAdministrationChanged`.
- Server administration request передаёт обязательное имя отдельно от внутреннего имени; object DTO/request передают числовой `Version`, а stale update/delete возвращает единый JSON envelope с HTTP `409 Conflict`.
- Новый клиентский код писать на TypeScript в `wwwroot/ts`, собранный JavaScript подключать из `wwwroot/dist`.
- `wwwroot/dist` является generated output TypeScript-сборки, не хранится в Git и восстанавливается штатной сборкой Web-проекта.
- `Microsoft.TypeScript.MSBuild` 7 использует targets `SetTscExecutable` и `RunTscCompile`; `PrepareTypeScriptForStaticWebAssets` выполняет их перед `ResolveProjectStaticWebAssets` и `ResolveCoreStaticWebAssets`, чтобы сборка восстанавливала `wwwroot/dist` до разрешения assets.
- `CleanTypeScriptOutputs` не включать в этот target: Static Web Assets фиксирует items при MSBuild evaluation, и удаление уже обнаруженных файлов перед resolution ломает сборку.
- MSBuild-свойства TypeScript должны сохранять контракт `tsconfig.json`: target `ES2022`, source maps отключены.
- Не использовать удалённые в TypeScript 7 targets `CheckFileSystemCaseSensitive`, `FindConfigFiles`, `TypeScriptDeleteOutputFromOtherConfigs` и `CompileTypeScriptWithTSConfig`.
- API главной панели размещено под `/api/dashboard`, требует аутентификацию и возвращает только отфильтрованные dashboard DTO без endpoint URL, с раздельными заданным и внутренним именами сервера, через `JsonResultMapper`; отдельного публичного `/api/objects` нет.
- Monitoring SignalR-событие содержит только тип и идентификатор объекта и отправляется сервером конкретным разрешённым user-id; клиент перечитывает snapshot после события и reconnect.
- Клиент объединяет SignalR refresh-события окном 200 мс и отменяет устаревший snapshot-fetch через `AbortController`; reconnect загружает snapshot немедленно.
- Автоматические `[ApiController]` model-state ошибки используют тот же `{ success: false, message, field? }` envelope, что и `JsonResultMapper`.
- Login имеет отдельную rate-limit policy; Web добавляет `nosniff`, `no-referrer`, запрет framing и применяемую CSP. SignalR browser client поставляется локально в `wwwroot/vendor/signalr/signalr.min.js` (версия 10.0.11, SHA-256 зафиксирован в технической документации).
- Административное API уведомлений размещено под `/api/admin/notifications`; token не входит в Web-контракты, а mutating actions и тестовая отправка требуют роль `Administrator` и antiforgery. Создание/редактирование чата и создание привязки выполняются в Bootstrap modal, таблица показывает отдельную строку на привязку с именем и ID чата.
- Административная страница пользователей размещена под `/Admin/Users`; создание и редактирование пользователя выполняются в отдельных Bootstrap modal, UI работает через `/api/admin/users` без перезагрузки, а операции изменения защищены antiforgery и ролью `Administrator`.
- Навигация для аутентифицированного пользователя показывает его логин непосредственно перед POST-выходом.
- `/Admin/Objects` показывает «Сервера», `/Admin/Containers` — отдельный общий список контейнеров. Создание и редактирование выполняются в Bootstrap modal. Сервер выбирает свободные/свои контейнеры через multiSelect; контейнер выбирает необязательный сервер через radioSelect. Общий GET/POST контейнеров размещён под `/api/admin/objects/containers`.
- Коллекции главной панели и административных страниц отображаются общими TypeScript-таблицами: фильтры слева, создание справа над списком, закреплённая шапка, внутренний scroll и сохранение ширины колонок. Тёмная Bootstrap-тема сохраняется, сложные заголовки и действия объясняются helpTooltip.
- Forwarded headers принимаются только из явно настроенной proxy-сети с ограничением `ForwardLimit` до одного прокси.
- Serilog выводит человекочитаемые строки в console и rolling-файлы `Logs/log_*.log` с UTC-временем, уровнем, source context и structured properties; каталог находится вне `wwwroot`, исключён из source/build/publish и монтируется как persistent container volume.
- Приложение принимает внутренний HTTP за Nginx и не использует `UseHttpsRedirection`; TLS завершается на reverse proxy, а исходная схема передаётся только через доверенные forwarded headers.
- File sink ограничен 10 MiB на сегмент и 31 файлом; connection string, пароли, Telegram token, cookies, request/response body и полный Telegram URI не логируются ни в один sink.
