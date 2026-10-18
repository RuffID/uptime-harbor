# Тестирование

## Test stack

Четыре test-проекта в каталоге `tests` используют общие настройки из `tests/Directory.Build.props`:

- `xunit.v3` 4.0.0;
- Visual Studio runner 4.0.0;
- Microsoft.NET.Test.Sdk 18.9.0;
- Microsoft Testing Platform;
- расширения code coverage и TRX report.

Корневой `global.json` выбирает .NET SDK 10.0.400 и runner `Microsoft.Testing.Platform`.

## Запуск

Каждый набор запускается отдельно:

```powershell
dotnet test --project .\tests\UptimeHarbor.Domain.Tests\UptimeHarbor.Domain.Tests.csproj
dotnet test --project .\tests\UptimeHarbor.Application.Tests\UptimeHarbor.Application.Tests.csproj
dotnet test --project .\tests\UptimeHarbor.Infrastructure.Tests\UptimeHarbor.Infrastructure.Tests.csproj
dotnet test --project .\tests\UptimeHarbor.Web.Tests\UptimeHarbor.Web.Tests.csproj
```

Старый синтаксис `dotnet test <project>` включает VSTest target и несовместим с выбранным runner на .NET 10.

Перед тестами затронутые production- и test-проекты собираются отдельно. Compile-check выполняется без запуска Web-хоста и с отдельным `BaseOutputPath` в `artifacts/compile-check`.

Статические Web-тесты ищут исходники на пять уровней выше `AppContext.BaseDirectory`. Для них отдельный output должен сохранять эту глубину, например `artifacts/compile-check/web-tests/Debug/net10.0`. Общий `artifacts/compile-check/Debug/net10.0` подходит для компиляции, но для Web source tests приводит к ошибкам поиска файлов. Корректировать нужно путь проверочного output, не создавать копии исходников за пределами решения.

## Выбор проверок при запрете БД

Полный Infrastructure-набор открывает SQLite in-memory и исполняет SQL. При запрете подключения к БД он не запускается целиком. Допустимый набор metadata/fake проверок выбирается повторяемым аргументом `--filter-class` после `--` в команде Microsoft Testing Platform:

- `UptimeHarbor.Tests.Infrastructure.Identity.InitialAdministratorBootstrapperTests`;
- `UptimeHarbor.Tests.Infrastructure.Monitoring.HttpMonitoringEndpointCheckerTests`;
- `UptimeHarbor.Tests.Infrastructure.Monitoring.MonitoringCycleRunnerTests`;
- `UptimeHarbor.Tests.Infrastructure.Persistence.UptimeHarborDbContextModelTests`;
- `UptimeHarbor.Tests.Infrastructure.Persistence.SqlServerPersistenceConflictClassifierTests`;
- `UptimeHarbor.Tests.Infrastructure.Telegram.TelegramHttpNotificationSenderTests`.

Metadata-тест создаёт SQL Server options и читает EF model без открытия соединения. Sign-in, materialized reads, concurrency, unique constraints и Telegram outbox provider tests в этот выбор не входят; их нельзя считать пройденными или пропущенными runner, если они исключены фильтром. Результат обновления локальных DLL и точная граница последней проверки приведены в [Libraries.md](Libraries.md).

## Структура и состав наборов

- `UptimeHarbor.Domain.Tests` — доменные переходы и фабрики;
- `UptimeHarbor.Application.Tests` — use case, политики, DTO и Application DI;
- `UptimeHarbor.Infrastructure.Tests` — persistence metadata и SQLite provider behavior, Identity bootstrap, monitoring/outbox workers и внешние адаптеры на fake transport;
- `UptimeHarbor.Web.Tests` — Web DI/configuration, authorization/antiforgery metadata, JSON mapping, SignalR и статические frontend-инварианты.
- `tests/Frontend/*.test.mts` — изолированные исполняемые проверки TypeScript через `node --experimental-strip-types --test tests/Frontend/*.test.mts`, без приложения и браузера.

Совокупный набор проверяет:

- доменные переходы серверов и контейнеров;
- validation options и DI регистрации;
- инфраструктурные registrations без реального подключения к БД;
- EF model metadata, каскады, ключи и indexes;
- optimistic concurrency через два независимых `UptimeHarborDbContext` на SQLite in-memory provider: monitoring/disable, monitoring/endpoint и два admin updates;
- UTC converter;
- notification binding factories;
- bootstrap администратора и повторный запуск;
- управление пользователями и permissions;
- единый `IAdministratorAccessPolicy`: unauthenticated, disabled, ordinary и administrator outcomes, одинаковая семантика трёх защищённых сервисов и отсутствие repository/UoW/save/Telegram/publisher вызовов при отказе;
- полная и ограниченная видимость, прямой доступ и блокировка;
- role metadata административного контроллера;
- административные сценарии серверов и контейнеров, валидация, независимые переключатели и уведомление после сохранения;
- role и antiforgery metadata административного API объектов;
- HTTP/JSON mapping всех типов `ServiceError`;
- полная DI validation Web composition root;
- DI-регистрацию database check-up/backup services и статический порядок automatic migration до Identity bootstrap;
- статические контракты Web-конфигурации: обязательный `Config/config.json` до DI, изоляция `appsettings.json` для Serilog, полнота и безопасность `config.example.json`, точное git-ignore правило и запрет копирования обоих Config JSON в runtime/publish artifacts;
- фактическое построение Serilog logger из `appsettings.json` без запуска Web-host и без записи log events;
- единый человекочитаемый UTC-шаблон console/rolling File sinks, непубличный относительный path, daily/size rolling, retention, исключение `Logs` из artifacts и защищённый Docker volume;
- наличие trusted forwarded headers до routing и отсутствие внутреннего HTTPS redirect в Web pipeline за Nginx;
- HTTP/JSON-контракт server/container через fake `HttpMessageHandler`, включая status, timeout/cancellation во время чтения, network, 64 KiB limit, chunked body, redirect policy и все обязательные поля;
- application-переходы monitoring engine, независимость server/container и порядок save→publish;
- параллельность разных объектов, keyed-защита одной цели, изоляция ошибки и cancellation hosted worker.
- dashboard query для администратора и пользователя, независимые container permissions, поиск, fallback имени, UTC и uptime через фиксированный `TimeProvider`;
- серверное определение SignalR-получателей, минимальный payload, изоляция push-ошибки, auth metadata и reconnect/snapshot lifecycle клиента.
- пакетное чтение recipient permissions без per-user вызовов и прежнюю server/container visibility semantics;
- read-only signatures всех списковых Application repository contracts, отсутствие `IQueryable` в публичных Application-контрактах и materialized dashboard/recipient read models;
- SQLite-проверку специализированных dashboard/recipient Infrastructure queries, отсутствия endpoint URL в projection и трёх пакетных recipient queries независимо от числа пользователей;
- SQLite-проверку уникального нормализованного имени сервера и metadata-проверку обязательных полей `Name`/`NormalizedName`;
- отсутствие endpoint URL в dashboard DTO, сохранение endpoint в admin DTO и отсутствие controller route `/api/objects`;
- best-effort administrative SignalR publisher: commit остаётся успешным при transport exception, ошибка журналируется, а до commit/conflict publisher не вызывается;
- фактически зарегистрированную `InvalidModelStateResponseFactory` и MVC JSON options для malformed body, отсутствующего body, неверного типа и field validation без parser details;
- безопасные ветви TypeScript HTTP helper для JSON/empty/non-JSON errors и `AbortError`, а также исходный coalescing/`AbortController`/reconnect wiring;
- Identity lockout через реальный `SignInManager` и SQLite in-memory store: порог, ограниченный срок, reset при успехе и независимость от `IsEnabled`;
- login-specific rate-limit metadata/rejection, Identity lockout metadata и выполнение middleware security headers/CSP без Web-host;
- per-binding состояние outage/recovery, частичный успех, повторная обработка только неуспешных получателей, независимые server/container bindings, fallback имени, parent server, UTC и uptime через fake `TimeProvider`;
- административные Telegram use case, роль и antiforgery metadata MVC API, AJAX/DOM-инварианты frontend;
- Telegram HTTP adapter через fake `HttpMessageHandler`, безопасные исключения и изоляция ошибки одного чата;
- формат Telegram token, отказ до HTTP при заглушке, корректный относительный Bot API URI и классификацию HTTP/JSON ошибок; Application проверяет безопасные сообщения тестовой отправки без утечки исходного текста исключения;
- геометрию ресайза через DOM-заглушку: номинальные ширины отличаются от фактических, pointerdown и повторный drag не меняют геометрию, дельта меняет только выбранную колонку, отмена восстанавливает ширину, клавиатура и минимальная ширина соблюдаются;
- durable outbox до/после нового DbContext, параллельное начало доставки чатов, независимость monitoring cycle и лимит пяти retry через управляемый `TimeProvider`;
- provider-level SQLite-сценарии recovery: outage marker → recovery queued → disable/endpoint change → terminal cancellation без sender, обычная recovery-доставка и безопасный pre-send skip уже claimed job;
- конкурентные duplicate chat/binding на двух SQLite `DbContext`, Application mapping независимого persistence conflict и безопасный HTTP 409 envelope без SQL/provider details;
- изолированный классификатор SQL Server 2601/2627 для известных Telegram entities, отказ классифицировать неизвестный номер/тип и проброс неизвестного `DbUpdateException`;
- отсутствие SignalR/outbox event после provider concurrency conflict и HTTP `409` для administrative conflict.

Web contract tests сериализуют `JsonResult` и administrative DTO через фактические MVC `JsonOptions`. Application проверяет self-disable/self-delete и last-enabled-admin. Runtime-проверка automatic SQL Server backup/migration, server-side backup permissions, concurrency, row-lock ordering dispatcher против administrative cancellation, фактические `SqlException` 2601/2627 вместе с EF entries, restart процесса и Telegram остаётся отдельной ручной проверкой; Docker/Nginx не входят в scope v1.

## Изоляция

Обычные тесты не обращаются к реальной БД, HTTP endpoint, Docker, PowerShell, процессам или внешним сервисам.

EF metadata tests не открывают соединение. Provider-level concurrency/outbox/unique tests используют только SQLite in-memory connection внутри процесса, без SQL Server, SQL-команд и Docker. SQLite подтверждает ограничения модели и контролируемые interleaving, но не доказывает SQL Server error numbers, lock granularity, `READ_COMMITTED_SNAPSHOT`, deadlock behavior или migration runtime. HTTP-тесты monitoring adapter используют управляемый fake handler и не создают сетевых соединений. Worker-тесты используют in-memory services и управляемые `TaskCompletionSource`.

Каждый test-файл явно импортирует `Xunit`; `GlobalUsings.cs` в проекте не используется.

SignalR-тесты используют fake `IHubContext` и не открывают WebSocket. Frontend lifecycle проверяется статически по TypeScript-источнику; реальный Web-хост и браузер в изолированном наборе не запускаются. Поэтому фактический browser-сценарий «30 быстрых событий → ограниченное число snapshot requests», реальная отмена fetch и настоящий MVC input formatter pipeline остаются ручными runtime-проверками и не считаются доказанными статическими тестами.

Web security tests статически проверяют отсутствие CDN в `Index.cshtml`, локальное подключение SignalR с versioning, наличие и SHA-256 asset версии 10.0.11, а также фактическое добавление CSP middleware и отсутствие запрещённых директив/origins. Реальная browser/runtime-проверка SignalR и CSP остаётся ручной и в изолированный набор не включается.

Configuration contract tests не запускают Web-host и поэтому не доказывают runtime-загрузку внешнего `Config/config.json`; отсутствие/ошибка JSON обеспечиваются параметрами обязательного configuration source и остаются частью будущей ручной проверки запуска.

Проверка доработок modal редактора объектов, полей поиска, переключателей и Telegram: Web и затронутые test-проекты собраны без предупреждений/ошибок; 15 transport-тестов, 16 Application notification administration tests, 41 выбранный Web-тест и 3 frontend-теста ресайза прошли. Изолированные проверки не подтверждают внешний вид/фокус в настоящем браузере и реальную доставку Telegram; приложение, БД и внешняя сеть не запускались.
