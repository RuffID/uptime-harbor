# Техническая документация

Документы предназначены разработчику и сопровождающему и описывают фактическое устройство системы. Бизнес-правила находятся в [ТЗ](../Business%20logic/Business%20requirements.md), исторические этапы — в [планах](../Plans/README.md).

| Область | Документ |
| --- | --- |
| Проекты, зависимости и composition root | [Architecture.md](Architecture.md) |
| Доменные сущности и инварианты | [Domain model.md](Domain%20model.md) |
| Identity, роли и видимость | [Authentication and visibility.md](Authentication%20and%20visibility.md) |
| EF Core, репозитории и транзакции | [Persistence.md](Persistence.md) |
| Общие данные, SQL-проекции, Pending и retention | [Shared cluster storage.md](Shared%20cluster%20storage.md) |
| Peer mTLS, DotNext, reducer/replay, очередь, snapshot и readiness | [Cluster runtime.md](Cluster%20runtime.md) |
| Локальные DLL, зависимости и обновление | [Libraries.md](Libraries.md) |
| Миграции и startup backup | [Database migrations.md](Database%20migrations.md) |
| Endpoint-проверки и monitoring worker | [Monitoring.md](Monitoring.md) |
| Telegram, accepted delivery ledger и локальный outbox | [Notifications.md](Notifications.md) |
| Razor Pages, API, TypeScript и SignalR | [Web application.md](Web%20application.md) |
| Внешняя конфигурация и секреты | [Configuration.md](Configuration.md) |
| Сборки, тесты и пределы доказательств | [Testing.md](Testing.md) |
| Развёртывание и эксплуатационные ограничения | [Deployment.md](Deployment.md) |
| Самостоятельная проверка ПК → production, первый запуск, join, membership, TLS, обслуживание и rollback | [Cluster operations.md](Cluster%20operations.md) |
| Backup/rejoin и ручное аварийное восстановление | [Cluster recovery.md](Cluster%20recovery.md) |
| Офлайн-инструмент и подписанные control-файлы | [Cluster control files.md](Cluster%20control%20files.md) |

## Исторические технические ревью

- [Первичное ревью первой версии](V1%20Technical%20Review%20Findings.md).
- [Повторное ревью первой версии](V1%20Follow-up%20Technical%20Review.md).

Ревью сохраняют выводы и результаты на момент проверки. Их исторические списки дефектов и счётчики тестов не заменяют актуальные тематические документы.

Сборки и тесты не означают готовность к runtime-релизу. Каждый документ должен отделять подтверждённые исходниками контракты от проверок реального окружения.
