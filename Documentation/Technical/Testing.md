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

## Структура и состав наборов

- `UptimeHarbor.Domain.Tests` — доменные переходы и фабрики;
- `UptimeHarbor.Application.Tests` — use case, политики, DTO и Application DI;
- `UptimeHarbor.Infrastructure.Tests` — persistence metadata и SQLite provider behavior, Identity bootstrap, monitoring/outbox workers и внешние адаптеры на fake transport;
- `UptimeHarbor.Web.Tests` — Web DI/configuration, authorization/antiforgery metadata, JSON mapping, SignalR и статические frontend-инварианты.

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
- HTTP/JSON-контракт server/container через fake `HttpMessageHandler`, включая status, timeout/cancellation во время чтения, network, 64 KiB limit, chunked body, redirect policy и все обязательные поля;
- application-переходы monitoring engine, независимость server/container и порядок save→publish;
- параллельность разных объектов, keyed-защита одной цели, изоляция ошибки и cancellation hosted worker.
- dashboard query для администратора и пользователя, независимые container permissions, поиск, fallback имени, UTC и uptime через фиксированный `TimeProvider`;
- серверное определение SignalR-получателей, минимальный payload, изоляция push-ошибки, auth metadata и reconnect/snapshot lifecycle клиента.
- пакетное чтение recipient permissions без per-user вызовов и прежнюю server/container visibility semantics;
- read-only signatures всех списковых Application repository contracts, отсутствие `IQueryable` в публичных Application-контрактах и materialized dashboard/recipient read models;
- SQLite-проверку специализированных dashboard/recipient Infrastructure queries, отсутствия endpoint URL в projection и трёх пакетных recipient queries независимо от числа пользователей;
- отсутствие endpoint URL в dashboard DTO, сохранение endpoint в admin DTO и отсутствие controller route `/api/objects`;
- best-effort administrative SignalR publisher: commit остаётся успешным при transport exception, ошибка журналируется, а до commit/conflict publisher не вызывается;
- фактически зарегистрированную `InvalidModelStateResponseFactory` и MVC JSON options для malformed body, отсутствующего body, неверного типа и field validation без parser details;
- безопасные ветви TypeScript HTTP helper для JSON/empty/non-JSON errors и `AbortError`, а также исходный coalescing/`AbortController`/reconnect wiring;
- Identity lockout через реальный `SignInManager` и SQLite in-memory store: порог, ограниченный срок, reset при успехе и независимость от `IsEnabled`;
- login-specific rate-limit metadata/rejection и выполнение middleware базовых security headers без Web-host;
- per-binding состояние outage/recovery, частичный успех, повторная обработка только неуспешных получателей, независимые server/container bindings, fallback имени, parent server, UTC и uptime через fake `TimeProvider`;
- административные Telegram use case, роль и antiforgery metadata MVC API, AJAX/DOM-инварианты frontend;
- Telegram HTTP adapter через fake `HttpMessageHandler`, безопасные исключения и изоляция ошибки одного чата;
- durable outbox до/после нового DbContext, параллельное начало доставки чатов, независимость monitoring cycle и лимит пяти retry через управляемый `TimeProvider`;
- отсутствие SignalR/outbox event после provider concurrency conflict и HTTP `409` для administrative conflict.

Web contract tests сериализуют `JsonResult` и administrative DTO через фактические MVC `JsonOptions`. Application проверяет self-disable/self-delete и last-enabled-admin. Runtime-проверка PostgreSQL migration/concurrency, restart процесса и Telegram остается отдельной ручной проверкой; Docker/Nginx не входят в scope v1.

## Изоляция

Обычные тесты не обращаются к реальной БД, HTTP endpoint, Docker, PowerShell, процессам или внешним сервисам.

EF metadata tests не открывают соединение. Provider-level concurrency/outbox tests используют только SQLite in-memory connection внутри процесса, без PostgreSQL, SQL-команд и Docker. HTTP-тесты monitoring adapter используют управляемый fake handler и не создают сетевых соединений. Worker-тесты используют in-memory services и управляемые `TaskCompletionSource`.

Каждый test-файл явно импортирует `Xunit`; `GlobalUsings.cs` в проекте не используется.

SignalR-тесты используют fake `IHubContext` и не открывают WebSocket. Frontend lifecycle проверяется статически по TypeScript-источнику; реальный Web-хост и браузер в изолированном наборе не запускаются. Поэтому фактический browser-сценарий «30 быстрых событий → ограниченное число snapshot requests», реальная отмена fetch и настоящий MVC input formatter pipeline остаются ручными runtime-проверками и не считаются доказанными статическими тестами.

Локальный SignalR browser asset и CSP не тестируются как реализованные: точный asset отсутствует локально, а запрещённые package manager/download операции не выполнялись.
