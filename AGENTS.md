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
- При содержательном изменении кода проверять и обновлять ближайший `AGENTS.md` и связанный файл в `Documentation/Technical`.
- Существующие планы `00`–`08` в `Documentation/Plans/Application implementation` считать запечатанной историей реализации независимо от указанного в них статуса: не менять их содержание, статус и отметки о проверках из-за последующего развития проекта.
- Изменять существующий план можно только по прямой просьбе пользователя изменить именно этот план. Новую работу оформлять новым планом или отражать в актуальной технической документации.
- Если назначение каталога, контракт или соглашение изменились, правка соответствующего `AGENTS.md` обязательна в той же задаче.
- Каждый новый файл в каталоге `Documentation` и его подкаталогах обязательно добавлять в соответствующую solution folder файла `uptime-harbor.slnx` в той же задаче.

## Проверка

- Production-проекты собирать отдельно без запуска Web-хоста.
- Изолированные тесты запускать через Microsoft Testing Platform отдельно для проектов в `tests`: `UptimeHarbor.Domain.Tests`, `UptimeHarbor.Application.Tests`, `UptimeHarbor.Infrastructure.Tests` и `UptimeHarbor.Web.Tests`.
