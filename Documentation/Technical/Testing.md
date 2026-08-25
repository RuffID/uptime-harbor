# Тестирование

## Test stack

`UptimeHarbor.Tests` использует:

- `xunit.v3` 4.0.0;
- Visual Studio runner 4.0.0;
- Microsoft.NET.Test.Sdk 18.9.0;
- Microsoft Testing Platform;
- расширения code coverage и TRX report.

Корневой `global.json` выбирает .NET SDK 10.0.400 и runner `Microsoft.Testing.Platform`.

## Запуск

Основная команда:

```powershell
dotnet test --project .\UptimeHarbor.Tests\UptimeHarbor.Tests.csproj
```

Старый синтаксис `dotnet test <project>` включает VSTest target и несовместим с выбранным runner на .NET 10.

Перед тестами затронутые production- и test-проекты собираются отдельно. Compile-check выполняется без запуска Web-хоста и с отдельным `BaseOutputPath` в `artifacts/compile-check`.

## Состав текущего набора

- доменные переходы серверов и контейнеров;
- validation options и DI регистрации;
- инфраструктурные registrations без реального подключения к БД;
- EF model metadata, каскады, ключи и indexes;
- UTC converter;
- notification binding factories;
- bootstrap администратора и повторный запуск;
- управление пользователями и permissions;
- полная и ограниченная видимость, прямой доступ и блокировка;
- role metadata административного контроллера;
- административные сценарии серверов и контейнеров, валидация, независимые переключатели и уведомление после сохранения;
- role и antiforgery metadata административного API объектов;
- HTTP/JSON mapping всех типов `ServiceError`;
- полная DI validation Web composition root;
- HTTP/JSON-контракт server/container через fake `HttpMessageHandler`, включая status, timeout, network и все обязательные поля;
- application-переходы monitoring engine, независимость server/container и порядок save→publish;
- параллельность разных объектов, keyed-защита одной цели, изоляция ошибки и cancellation hosted worker.
- dashboard query для администратора и пользователя, независимые container permissions, поиск, fallback имени, UTC и uptime через фиксированный `TimeProvider`;
- серверное определение SignalR-получателей, минимальный payload, изоляция push-ошибки, auth metadata и reconnect/snapshot lifecycle клиента.

После завершения этапа главной панели и SignalR набор содержит 129 изолированных тестов.

## Изоляция

Обычные тесты не обращаются к реальной БД, HTTP endpoint, Docker, PowerShell, процессам или внешним сервисам.

EF model tests создают DbContext с provider options, но не открывают соединение. HTTP-тесты monitoring adapter используют управляемый fake handler и не создают сетевых соединений. Worker-тесты используют in-memory services и управляемые `TaskCompletionSource`.

Каждый test-файл явно импортирует `Xunit`; `GlobalUsings.cs` в проекте не используется.

SignalR-тесты используют fake `IHubContext` и не открывают WebSocket. Frontend lifecycle проверяется статически по TypeScript-источнику; реальный Web-хост и браузер в изолированном наборе не запускаются.
