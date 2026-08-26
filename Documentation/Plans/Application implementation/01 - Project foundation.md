# Этап 1. Основа проекта

## Статус

**Завершён.** Решение разделено на отдельные проекты, зависимости проверены сборкой и тестами.

## Цель

Подготовить решение ASP.NET Core 10 из отдельных проектов Domain, Application, Infrastructure и Web к дальнейшей разработке без реализации бизнес-функций.

## Работы

1. Создать четыре основных проекта:

   - `UptimeHarbor.Domain` — class library с сущностями, статусами и правилами переходов;
   - `UptimeHarbor.Application` — class library с use case, прикладными сервисами, DTO, контрактами репозиториев и сценарными Unit of Work;
   - `UptimeHarbor.Infrastructure` — class library с EF Core, Identity, PostgreSQL, Data Protection, HTTP и Telegram;
   - `UptimeHarbor.Web` — исполняемый ASP.NET Core Razor Pages-проект с `Program.cs`, `Pages`, `Hubs` и Web-конфигурацией.

2. Настроить ссылки между проектами:

   - Application ссылается только на Domain;
   - Infrastructure ссылается на Application и Domain;
   - Web ссылается на Application и Infrastructure;
   - Domain не содержит ссылок на остальные проекты решения, EF Core и ASP.NET Core;
   - циклические и обратные ссылки отсутствуют.

3. Подключить пакеты и локальные библиотеки только в использующие их проекты:

   - Entity Framework Core для PostgreSQL, ASP.NET Core Identity с EF Core, `EFCoreLibrary.dll` и `HttpClientLibrary.dll` — в Infrastructure;
   - SignalR — в Web, если он не предоставляется shared framework;
   - Telegram-клиент либо небольшой адаптер над официальным Bot API — в Infrastructure.

4. Оставить в `UptimeHarbor.Web/Program.cs` только:

   - создание builder;
   - включение DI validation в development;
   - вызовы extension-методов регистрации модулей;
   - настройку middleware;
   - mapping Razor Pages, SignalR и health endpoint;
   - запуск приложения.

5. Создать типизированные настройки в слое, который владеет соответствующей интеграцией, и регистрировать их из Web composition root:

   - `BootstrapAdminOptions`;
   - `MonitoringOptions`;
   - `TelegramOptions`.

6. Добавить fail-fast валидацию обязательных настроек.
7. Определить единый способ получения времени через `TimeProvider` для тестируемости логики UTC.
8. Подготовить стандарт структурированного логирования без вывода секретов.
9. Создать отдельные test-проекты Domain, Application, Infrastructure и Web с явными ссылками на проверяемые слои и общими соглашениями в `tests/Directory.Build.props`.

## Проверки

- все четыре основных проекта собираются под `net10.0`;
- ссылки между проектами соответствуют разрешённому направлению зависимостей;
- Domain не имеет ссылок на EF Core, ASP.NET Core и Infrastructure;
- DI-контейнер валидируется в development;
- некорректные обязательные options препятствуют запуску с понятным сообщением;
- `Program.cs` не содержит прикладной логики;
- все test-проекты обнаруживаются средством запуска тестов.

## Результат этапа

Получен компилируемый каркас решения из Domain, Application, Infrastructure и Web с компиляционно закреплёнными архитектурными границами, конфигурацией и отдельными test-проектами для каждого слоя.
