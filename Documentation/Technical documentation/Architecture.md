# Архитектура решения

## Текущее состояние

Решение состоит из восьми SDK-style проектов под `net10.0`:

- `UptimeHarbor.Domain` — доменные сущности и локальные инварианты;
- `UptimeHarbor.Application` — use case управления пользователями, видимостью, объектами и результатами мониторинга, DTO, endpoint/event-контракты, независимые репозитории и сценарные Unit of Work;
- `UptimeHarbor.Infrastructure` — EF Core, Identity, persistence, HTTP/JSON-адаптер и фоновый worker;
- `UptimeHarbor.Web` — исполняемый ASP.NET Core-проект и composition root;
- `tests/UptimeHarbor.Domain.Tests` — изолированные тесты Domain;
- `tests/UptimeHarbor.Application.Tests` — изолированные тесты Application;
- `tests/UptimeHarbor.Infrastructure.Tests` — изолированные тесты Infrastructure;
- `tests/UptimeHarbor.Web.Tests` — изолированные тесты Web и composition root.

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

Test-проекты ссылаются только на проверяемый production-слой и его необходимые зависимости. Общий test stack задаётся в `tests/Directory.Build.props`, а Web composition root и configuration проверяются в `UptimeHarbor.Web.Tests`, поэтому Infrastructure-тесты не зависят от Web.

Эти ссылки являются компиляционной защитой границ. Domain и Application не должны получать EF Core, ASP.NET Core, Web или integration-типы даже ради удобства реализации.

## Composition root

`UptimeHarbor.Web/Program.cs` создаёт builder, включает DI validation в Development, вызывает модульные extension-методы, настраивает middleware и сопоставляет endpoints.

Регистрации распределены по модулям:

- `AddApplication` — `TimeProvider.System`, единая administrator access policy, управление пользователями и объектами, dashboard/recipient visibility use case и publisher мониторинга;
- `AddApplicationConfiguration` — options и их fail-fast валидация;
- `AddInfrastructure` — база данных, Identity/cookies, bootstrap администратора, репозитории, специализированные materialized read queries, Unit of Work, Data Protection, HTTP-клиенты и monitoring worker;
- `AddWebPresentation` — контроллеры, Razor Pages, SignalR и publisher административных изменений.

## Границы моделей

Доменные сущности не являются Web-моделями или DTO. Application-контракты оперируют доменными типами внутри серверной части, но будущие use case должны возвращать отдельные DTO на границах UI и интеграций.

Identity-типы находятся в Infrastructure, потому что наследование от ASP.NET Core Identity является технической деталью хранения и аутентификации.

Этап 02 добавил `Domain/Cluster`, storage-контракты и узкие UoW: SQL — восстанавливаемая проекция Raft log + snapshot. Общий аккаунт, роль Administrator и cursor сохраняются одним projection UoW; локальные Pending/capture/results отделены. Этап 03 подключил DotNext 6.10.0, отдельный peer-периметр, recovery/readiness и sender authority. Recovery, accepted account/trust и принятые renewal/attempt decisions пока закрыты независимыми контрактами 04/05/07. Reducer, общий вход и распределённые workers не реализованы. В enabled-режиме прежние workers, sender, UI и startup DB/bootstrap не выполняются. Схема — [Shared cluster storage.md](Shared%20cluster%20storage.md), порядок старта и протокол — [Cluster runtime.md](Cluster%20runtime.md).

Application получает сведения о текущем пользователе через независимый `IUserAccessContext`, а операции Identity — через `IUserAccountStore`. `IAdministratorAccessPolicy` единообразно возвращает semantic `Unauthorized`/`Forbidden` для `UserManagementService`, `ObjectAdministrationService` и `NotificationAdministrationService`; каждый use case вызывает policy до I/O. Ожидаемые ошибки сценариев описываются `ServiceResult` и переводятся в HTTP-статусы только в Web.

`ObjectAdministrationService` проверяет административный доступ через общую policy внутри каждого сценария и использует отдельный `IObjectAdministrationUnitOfWork`. После успешного сохранения он вызывает независимый `IObjectAdministrationChangePublisher`; Web-реализация best-effort отправляет обезличенный SignalR-сигнал, изолирует transport exception и не меняет уже успешный результат commit.

`MonitoringService` получает независимые результаты server/container endpoint, повторно проверяет актуальность включённого объекта и endpoint, вызывает доменные переходы и сохраняет через `IMonitoringUnitOfWork`. Optimistic concurrency отклоняет stale write. Outage/recovery payload добавляется в durable outbox в том же `SaveChangesAsync`, а `MonitoringStateChangedEvent` для SignalR публикуется только после успешного сохранения. HTTP, JSON, monitoring worker, Telegram outbox dispatcher и keyed-координация остаются в Infrastructure.

`DashboardQuery` зависит от `IDashboardSnapshotReadQuery`, `IUserAccessContext` и `TimeProvider`. Infrastructure query проецирует materialized read records без endpoint URL; Application применяет сохранённую чистую `ObjectVisibilityPolicy`, выполняет поиск и рассчитывает uptime относительно UTC-времени snapshot. Администратор получает все цели; неадминистративный compatibility-вызов — пустой набор. `MonitoringEventRecipientResolver` использует `IMonitoringRecipientReadQuery`: users и Administrator читаются одним запросом, коллекции прежних permissions пусты. Целевых таблиц resource permissions нет; старые UI/use case ещё не переработаны в этапе 05.

Web-обработчик внутреннего monitoring event определяет разрешённые Identity user-id в Application, а через SignalR отправляет только тип и GUID изменившегося объекта. Клиент использует событие как сигнал перечитать защищённые данные из SQL. Существующие однопроцессные workers продолжают локальный поток; для принятых общих данных источник истины — Raft log + snapshot, SQL-проекция сама принятие не подтверждает.

## Правила сопровождения

Архитектурное изменение должно одновременно обновлять project references, ближайшие `AGENTS.md` и актуальную техническую документацию. Статусы обновляются только у относящихся к работе активных планов; исторические результаты не актуализируются вслед за кодом. Навигация по документации находится в [README.md](README.md), правила новых планов — в [Plans/README.md](../Plans/README.md).
