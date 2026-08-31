# tests

## Ответственность

Каталог содержит изолированные тесты, разделённые по проверяемым слоям:

- `UptimeHarbor.Domain.Tests` — доменные сущности и локальные инварианты;
- `UptimeHarbor.Application.Tests` — Application use case, политики и DI-регистрации;
- `UptimeHarbor.Infrastructure.Tests` — Identity bootstrap, HTTP/Telegram-адаптеры, worker и EF model metadata;
- `UptimeHarbor.Web.Tests` — Web composition root, configuration, authorization/antiforgery metadata, SignalR, JSON mapping и статические frontend-проверки.

Общие настройки test stack находятся в `Directory.Build.props`. Каждый test-проект содержит только ссылки на проверяемый слой и его необходимые зависимости.

## Правила

- Использовать xUnit v3 и явный `using Xunit;`; не создавать `GlobalUsings.cs`.
- Не обращаться к реальной БД, HTTP endpoint, Docker, процессам и внешним сервисам в обычном наборе.
- Домен тестировать через публичный API сущностей, не ослабляя инварианты ради теста.
- Persistence model проверять через metadata без открытия соединения.
- Запускать каждый проект через `dotnet test --project .\tests\<project>\<project>.csproj`.
- При изменении структуры или типа тестов обновлять этот файл и `Documentation/Technical/Testing.md`.
- HTTP-проверки тестировать через управляемый fake `HttpMessageHandler`; timeout и текущее время контролировать через options и `TimeProvider` без внешних endpoint.
- Параллельность worker проверять через управляемые `TaskCompletionSource`, не используя реальные задержки, кроме изолированной проверки timeout.
- Dashboard query проверять на серверную видимость, поиск, UTC и uptime с фиксированным `TimeProvider`; SignalR — через fake hub context без запуска Web-хоста.
- Общую administrator policy проверять отдельно и через все три защищённых сервиса с fail-fast зависимостями; materialized dashboard/recipient queries проверять на SQLite без внешней БД и на отсутствие N+1.
- Telegram transport проверять fake `HttpMessageHandler`; частичную доставку — in-memory repositories/scopes, без сети и БД; frontend — статически по TypeScript/Razor исходникам.
- Web contract tests должны сериализовать DTO через фактические MVC `JsonOptions`, а не через произвольные `JsonSerializerOptions`.
- Web configuration contract tests статически проверяют composition root, отсутствие внутреннего HTTPS redirect за reverse proxy, project/package metadata, `.gitignore`, publish metadata, Docker log volume и безопасные JSON-примеры; Serilog-конфигурация с человекочитаемыми console и rolling File sinks строится без запуска Web-host и без записи секретов.
- Web DI/static tests проверяют регистрацию database backup/check-up services и порядок migration startup до Identity bootstrap без подключения к SQL Server и без выполнения SQL.
- Model-state tests вызывают фактически зарегистрированную `InvalidModelStateResponseFactory`; TypeScript lifecycle проверяется статически, а реальные browser burst/cancellation сценарии не выдаются за автоматические.
- Identity lockout проверяется реальным `SignInManager` и EF SQLite in-memory store; login rate-limit и security headers — зарегистрированными Web options/metadata и middleware без запуска Web-host.
- Optimistic concurrency и durable outbox проверять через два независимых `UptimeHarborDbContext` на SQLite in-memory provider; EF InMemory и metadata-only проверки не заменяют provider behavior.
- Telegram retry проверять с управляемым `TimeProvider`; зависания чатов — через `TaskCompletionSource` без внешней сети.
- Recovery cancellation проверять SQLite interleaving «claim → cancel → pre-send gate» и terminal payload; не выдавать это за доказательство SQL Server row-lock semantics.
- SQL Server 2601/2627 проверять изолированным классификатором; SQLite unique conflict не считать проверкой номеров или структуры `SqlException`.
