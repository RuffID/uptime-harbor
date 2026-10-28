# UptimeHarbor

## Назначение репозитория

UptimeHarbor — Web-приложение для контроля доступности серверов и приложений по HTTP endpoint, разграничения видимости и отправки Telegram-уведомлений.

## Структура

- `UptimeHarbor.Domain` — доменная модель и локальные инварианты.
- `UptimeHarbor.Application` — use case, DTO и независимые контракты доступа к данным.
- `UptimeHarbor.Infrastructure` — EF Core, Identity и внешние адаптеры.
- `UptimeHarbor.Web` — composition root, Razor Pages, контроллеры и SignalR.
- `tests` — четыре отдельных тестовых проекта Domain, Application, Infrastructure и Web.
- `libs` — локальные DLL EFCoreLibrary и HttpClientLibrary; источник и зависимости описаны в `Documentation/Technical documentation/Libraries.md`.
- `Documentation` — бизнес-требования, техническое описание и планы.

## Обязательные правила

- Соблюдать направление зависимостей `Web → Infrastructure → Application → Domain`.
- Не переносить EF Core, ASP.NET Core и интеграционные детали в Domain или Application.
- Не создавать глобальный Unit of Work; использовать сценарные Unit of Work и `IUnitOfWorkScope`.
- При содержательном изменении кода проверять и обновлять ближайший `AGENTS.md` и связанный файл в `Documentation/Technical documentation`.
- Завершённые планы считать историей реализации: не актуализировать их содержание и проверки вслед за кодом. Изменение или удаление исторического плана требует прямой просьбы пользователя.
- Изменять существующий план можно только по прямой просьбе пользователя изменить именно этот план. Новую работу оформлять новым планом или отражать в актуальной технической документации.
- Если назначение каталога, контракт или соглашение изменились, правка соответствующего `AGENTS.md` обязательна в той же задаче.
- Каждый новый файл в каталоге `Documentation` и его подкаталогах обязательно добавлять в соответствующую solution folder файла `uptime-harbor.slnx` в той же задаче.
- Навигация по документации находится в `Documentation/README.md`, по техническим областям — в `Documentation/Technical documentation/README.md`, по планам — в `Documentation/Plans/README.md`. Общую документацию не копировать в проекты и не подключать повторно через `.csproj`.
- В папках с собственным кодом, включая тесты, поддерживать локальные `AGENTS.md` с местными обязанностями и ограничениями без копирования родительских правил. Исключать generated output, migrations, сторонние библиотеки и папки только со служебными файлами.

## Проверка

- Production-проекты собирать отдельно без запуска Web-хоста.
- Изолированные тесты запускать через Microsoft Testing Platform отдельно для проектов в `tests`: `UptimeHarbor.Domain.Tests`, `UptimeHarbor.Application.Tests`, `UptimeHarbor.Infrastructure.Tests` и `UptimeHarbor.Web.Tests`.
