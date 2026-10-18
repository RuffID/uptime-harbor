# UptimeHarbor

Web-приложение для проверки HTTP endpoint серверов и приложений, разграничения видимости и Telegram-уведомлений. Использует ASP.NET Core 10, Razor Pages, SignalR и Microsoft SQL Server.

## Документация

- [Навигация по документации](Documentation/README.md).
- [Бизнес-требования и границы первой версии](Documentation/Business%20logic/Business%20requirements.md).
- [Текущее техническое устройство](Documentation/Technical%20documentation/README.md).
- [Планы и исторические материалы](Documentation/Plans/README.md).
- [Локальные DLL библиотек](Documentation/Technical%20documentation/Libraries.md).

## Структура решения

Четыре production-проекта находятся в отдельных физических папках: `UptimeHarbor.Domain`, `UptimeHarbor.Application`, `UptimeHarbor.Infrastructure`, `UptimeHarbor.Web`. Четыре тестовых проекта находятся в `tests`. Общая `Documentation` хранится только в корне и подключена через solution items в `uptime-harbor.slnx`.

`libs` содержит локальную поставку EFCoreLibrary и HttpClientLibrary. Их исходники и порядок обновления описаны в [документе библиотек](Documentation/Technical%20documentation/Libraries.md).

## Проверка и эксплуатация

[Testing.md](Documentation/Technical%20documentation/Testing.md) описывает сборки и границы изолированных проверок. Для запуска нужны внешняя конфигурация и SQL Server; порядок описан в [Configuration.md](Documentation/Technical%20documentation/Configuration.md), [Database migrations.md](Documentation/Technical%20documentation/Database%20migrations.md) и [Deployment.md](Documentation/Technical%20documentation/Deployment.md).

Прохождение сборок и изолированных тестов не подтверждает применение миграций, работу SQL Server, браузера, Telegram или развёртывания. Исторические планы и отчёты ревью сохраняют состояние на момент их составления.
