# План реализации UptimeHarbor

## Статус

**В работе.** Follow-up P1 recovery cancellation и P2 Telegram unique conflicts исправлены в коде и разрешённых SQLite/fake/classifier tests. Web разделяет Serilog-only `appsettings.json` и обязательный внешний `Config/config.json`; persistence использует SQL Server и автоматически создаёт backup/применяет pending migrations до Identity bootstrap. Browser burst/cancellation, CSP/SignalR runtime, настоящий HTTP formatter pipeline, runtime-загрузка config, реальный SQL Server backup/migration, locking/2601/2627, process restart и Telegram runtime остаются ручными. Этап 8 приостановлен: Docker Compose и orchestration не входят в v1.

## Цель

Реализовать первую односерверную версию UptimeHarbor в решении ASP.NET Core 10 с отдельными проектами Domain, Application, Infrastructure и Web в соответствии с [техническим заданием](../../Technical%20requirements.md).

## Структура решения

- `UptimeHarbor.Domain` — доменные сущности, статусы, value object и инварианты; не зависит от других проектов решения;
- `UptimeHarbor.Application` — use case, прикладные сервисы, DTO, контракты репозиториев и сценарные Unit of Work; зависит только от Domain;
- `UptimeHarbor.Infrastructure` — EF Core, SQL Server, Identity, Data Protection, HTTP и Telegram; зависит от Application и Domain;
- `UptimeHarbor.Web` — Razor Pages, SignalR hubs, Web-конфигурация и composition root; зависит от Application и Infrastructure;
- тестовые проекты ссылаются только на проверяемые слои и необходимые им зависимости.

Допустимое направление ссылок:

```text
UptimeHarbor.Web → UptimeHarbor.Infrastructure → UptimeHarbor.Application → UptimeHarbor.Domain
                        └───────────────────────────────────────────────→ UptimeHarbor.Domain
```

## Общие принципы

- сохранять приложение простым и не вводить распределённую инфраструктуру;
- разрабатывать функциональность небольшими завершёнными этапами;
- на каждом этапе добавлять проверки критичной логики;
- хранить все даты и время в UTC;
- контролировать архитектурные границы ссылками между проектами, а не только структурой каталогов;
- не связывать Web-интерфейс напрямую с EF Core или Telegram API;
- не размещать прикладную логику и прямые регистрации зависимостей в `Program.cs`;
- не приступать к следующему этапу, пока текущий не собирается и не проходит относящиеся к нему тесты.

## Последовательность этапов

1. [Основа проекта](01%20-%20Project%20foundation.md)
2. [Данные и хранение](02%20-%20Data%20and%20persistence.md)
3. [Аутентификация и видимость](03%20-%20Authentication%20and%20visibility.md)
4. [Администрирование объектов](04%20-%20Object%20administration.md)
5. [Движок мониторинга](05%20-%20Monitoring%20engine.md)
6. [Главная страница и SignalR](06%20-%20Dashboard%20and%20SignalR.md)
7. [Telegram-уведомления](07%20-%20Telegram%20notifications.md)
8. [Развёртывание и выпуск](08%20-%20Deployment%20and%20release.md)

## Контрольные точки

### После этапа 2

Приложение подключается к SQL Server, применяет миграции согласованным способом и сохраняет модель данных.

### После этапа 4

Администратор может войти, создать пользователей, серверы и контейнеры, назначить видимость и проверить каскадное удаление.

### После этапа 6

Работает основной пользовательский сценарий: проверки endpoint, актуальные статусы, поиск и обновления через SignalR.

### После этапа 8

Автоматизация односерверного развёртывания через Docker Compose за Nginx не входит в первую версию.

## Необходимая документация по мере реализации

- описание внешней production-конфигурации без настоящих секретов;
- описание ручного первого запуска;
- описание обновления схемы SQL Server;
- краткая инструкция добавления Telegram-бота в чат;
- перечень известных ограничений первой версии.
