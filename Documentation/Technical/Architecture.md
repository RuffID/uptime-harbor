# Архитектура решения

## Текущее состояние

Решение состоит из пяти SDK-style проектов под `net10.0`:

- `UptimeHarbor.Domain` — доменные сущности и локальные инварианты;
- `UptimeHarbor.Application` — use case управления пользователями, видимостью и объектами мониторинга, DTO, result-контракты, независимые репозитории и сценарные Unit of Work;
- `UptimeHarbor.Infrastructure` — EF Core, Identity, persistence и интеграционные библиотеки;
- `UptimeHarbor.Web` — исполняемый ASP.NET Core-проект и composition root;
- `UptimeHarbor.Tests` — общий изолированный test-проект.

## Направление зависимостей

Допустимые project references:

```text
UptimeHarbor.Web ───────→ UptimeHarbor.Infrastructure
        │                            │
        └──────────────→ UptimeHarbor.Application ─────→ UptimeHarbor.Domain
                                     ↑
UptimeHarbor.Infrastructure ─────────┘
        │
        └──────────────────────────────────────────────→ UptimeHarbor.Domain
```

`Domain` не имеет project references. `Application` ссылается только на `Domain`. `Infrastructure` ссылается на `Application` и `Domain`. `Web` ссылается на `Application` и `Infrastructure`.

Эти ссылки являются компиляционной защитой границ. Domain и Application не должны получать EF Core, ASP.NET Core, Web или integration-типы даже ради удобства реализации.

## Composition root

`UptimeHarbor.Web/Program.cs` создаёт builder, включает DI validation в Development, вызывает модульные extension-методы, настраивает middleware и сопоставляет endpoints.

Регистрации распределены по модулям:

- `AddApplication` — `TimeProvider.System`, управление пользователями и объектами, единый сервис видимости;
- `AddApplicationConfiguration` — options и их fail-fast валидация;
- `AddInfrastructure` — база данных, Identity/cookies, bootstrap администратора, репозитории, Unit of Work, Data Protection и HTTP-клиент;
- `AddWebPresentation` — контроллеры, Razor Pages, SignalR, publisher административных изменений и health checks.

## Границы моделей

Доменные сущности не являются Web-моделями или DTO. Application-контракты оперируют доменными типами внутри серверной части, но будущие use case должны возвращать отдельные DTO на границах UI и интеграций.

Identity-типы находятся в Infrastructure, потому что наследование от ASP.NET Core Identity является технической деталью хранения и аутентификации.

Application получает сведения о текущем пользователе через независимый `IUserAccessContext`, а операции Identity — через `IUserAccountStore`. Ожидаемые ошибки сценариев описываются `ServiceResult` и переводятся в HTTP-статусы только в Web.

`ObjectAdministrationService` повторно проверяет административную роль внутри каждого сценария и использует отдельный `IObjectAdministrationUnitOfWork`. После успешного сохранения он вызывает независимый `IObjectAdministrationChangePublisher`; Web-реализация отправляет обезличенный SignalR-сигнал о необходимости перечитать доступные данные.

## Правила сопровождения

Архитектурное изменение должно одновременно обновлять project references, ближайшие `AGENTS.md`, этот документ и статус соответствующего плана.
