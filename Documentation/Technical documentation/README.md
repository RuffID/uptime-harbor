# Техническая документация

Документы предназначены разработчику и сопровождающему и описывают фактическое устройство системы. Бизнес-правила находятся в [ТЗ](../Business%20logic/Business%20requirements.md), исторические этапы — в [планах](../Plans/README.md).

| Область | Документ |
| --- | --- |
| Проекты, зависимости и composition root | [Architecture.md](Architecture.md) |
| Доменные сущности и инварианты | [Domain model.md](Domain%20model.md) |
| Identity, роли и видимость | [Authentication and visibility.md](Authentication%20and%20visibility.md) |
| EF Core, репозитории и транзакции | [Persistence.md](Persistence.md) |
| Общие данные, SQL-проекции, Pending и retention | [Shared cluster storage.md](Shared%20cluster%20storage.md) |
| Приглашения, попытки, delegation и versioned data contracts; production допуск закрыт | [Onboarding contracts.md](Onboarding%20contracts.md) |
| Peer mTLS, DotNext, reducer/replay, очередь, snapshot и readiness | [Cluster runtime.md](Cluster%20runtime.md) |
| Локальные DLL, зависимости и обновление | [Libraries.md](Libraries.md) |
| Миграции и startup backup | [Database migrations.md](Database%20migrations.md) |
| Endpoint-проверки и monitoring worker | [Monitoring.md](Monitoring.md) |
| Подготовка Ubuntu: HTTPS на 43871 в существующем nginx с готовым сертификатом Let's Encrypt, время загрузки в UTC | [Ubuntu server monitoring.md](Ubuntu%20server%20monitoring.md) |
| Подключение контейнеров: JSON endpoint ASP.NET Core, nginx и добавление в проект | [ASP.NET Core container monitoring.md](ASP.NET%20Core%20container%20monitoring.md) |
| Telegram, accepted delivery ledger и локальный outbox | [Notifications.md](Notifications.md) |
| Razor Pages, API, TypeScript и SignalR | [Web application.md](Web%20application.md) |
| Внешняя конфигурация и секреты | [Configuration.md](Configuration.md) |
| Сборки, тесты и пределы доказательств | [Testing.md](Testing.md) |
| Развёртывание и эксплуатационные ограничения | [Deployment.md](Deployment.md) |
| Самостоятельная проверка ПК → production, первый запуск, join, membership, TLS, обслуживание и rollback | [Cluster operations.md](Cluster%20operations.md) |
| Backup/rejoin и ручное аварийное восстановление | [Cluster recovery.md](Cluster%20recovery.md) |
| Офлайн-инструмент и подписанные control-файлы | [Cluster control files.md](Cluster%20control%20files.md) |

Сборки и тесты не означают готовность к runtime-релизу. Каждый документ должен отделять подтверждённые исходниками контракты от проверок реального окружения.
