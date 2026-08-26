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
- Telegram transport проверять fake `HttpMessageHandler`; частичную доставку — in-memory repositories/scopes, без сети и БД; frontend — статически по TypeScript/Razor исходникам.
