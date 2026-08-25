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
- notification binding factories.

Текущий набор содержит 28 изолированных тестов.

## Изоляция

Обычные тесты не обращаются к реальной БД, HTTP endpoint, Docker, PowerShell, процессам или внешним сервисам.

EF model tests создают DbContext с provider options, но не открывают соединение. Будущие HTTP-тесты должны использовать управляемый fake handler.

Каждый test-файл явно импортирует `Xunit`; `GlobalUsings.cs` в проекте не используется.
