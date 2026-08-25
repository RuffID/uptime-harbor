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
