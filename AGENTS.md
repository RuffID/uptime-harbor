# UptimeHarbor

## Назначение репозитория

UptimeHarbor — Web-приложение для контроля доступности серверов и приложений по HTTP endpoint, разграничения видимости и отправки Telegram-уведомлений.

## Структура

- `UptimeHarbor.Domain` — доменная модель и локальные инварианты.
- `UptimeHarbor.Application` — use case, DTO и независимые контракты доступа к данным.
- `UptimeHarbor.Infrastructure` — EF Core, Identity и внешние адаптеры.
- `UptimeHarbor.Web` — composition root, Razor Pages, контроллеры и SignalR.
- `UptimeHarbor.Tests` — изолированные тесты всех слоёв.
- `Documentation` — бизнес-требования, техническое описание и планы.

## Обязательные правила

- Соблюдать направление зависимостей `Web → Infrastructure → Application → Domain`.
- Не переносить EF Core, ASP.NET Core и интеграционные детали в Domain или Application.
- Не создавать глобальный Unit of Work; использовать сценарные Unit of Work и `IUnitOfWorkScope`.
- При содержательном изменении кода проверять и обновлять ближайший `AGENTS.md`, связанный файл в `Documentation/Technical` и статус затронутого плана.
- Если назначение каталога, контракт или соглашение изменились, правка соответствующего `AGENTS.md` обязательна в той же задаче.

## Проверка

- Production-проекты собирать отдельно без запуска Web-хоста.
- Изолированные тесты запускать через Microsoft Testing Platform отдельно для проектов в `tests`: `UptimeHarbor.Domain.Tests`, `UptimeHarbor.Application.Tests`, `UptimeHarbor.Infrastructure.Tests` и `UptimeHarbor.Web.Tests`.
