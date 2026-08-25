# UptimeHarbor.Tests

## Ответственность

Проект содержит изолированные тесты домена, Application use case, Identity bootstrap, DI, Web authorization/antiforgery metadata, JSON mapping и EF model metadata.

## Правила

- Использовать xUnit v3 и явный `using Xunit;`; не создавать `GlobalUsings.cs`.
- Не обращаться к реальной БД, HTTP endpoint, Docker, процессам и внешним сервисам в обычном наборе.
- Домен тестировать через публичный API сущностей, не ослабляя инварианты ради теста.
- Persistence model проверять через metadata без открытия соединения.
- Запускать через `dotnet test --project .\UptimeHarbor.Tests\UptimeHarbor.Tests.csproj`.
- При изменении структуры или типа тестов обновлять этот файл и `Documentation/Technical/Testing.md`.
- HTTP-проверки тестировать через управляемый fake `HttpMessageHandler`; timeout и текущее время контролировать через options и `TimeProvider` без внешних endpoint.
- Параллельность worker проверять через управляемые `TaskCompletionSource`, не используя реальные задержки, кроме изолированной проверки timeout.
- Dashboard query проверять на серверную видимость, поиск, UTC и uptime с фиксированным `TimeProvider`; SignalR — через fake hub context без запуска Web-хоста.
