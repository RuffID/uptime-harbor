# Сборка и развёртывание

## Состояние реализации

В Web-проекте есть multi-stage Dockerfile, который восстанавливает и собирает Domain, Application, Infrastructure и Web, копирует локальные DLL из `libs` и запускает `UptimeHarbor.Web.dll` на ASP.NET runtime image. Deployment-шаблоны, Nginx-конфигурация, Docker Compose и orchestration не входят в требования первой версии и в репозитории не поставляются.

## Границы первой версии

Первая версия не фиксирует целевую схему reverse proxy, контейнеров или orchestration. Выбор средств запуска Web и SQL Server, сетевой изоляции и хранения данных остаётся ответственностью окружения.

## Конфигурация и данные

Production-конфигурация и секреты должны задаваться средствами целевого окружения вне репозитория. Данные SQL Server и Identity Data Protection keys (таблица `data_protection_keys`) должны сохраняться средствами этого окружения.

Serilog пишет человекочитаемые логи в console и rolling-файлы в `/app/Logs`. Dockerfile создаёт каталог с владельцем `$APP_UID`, режимом `0700` и объявляет его volume. Для контролируемого хранения следует подключить named volume или bind mount; bind mount должен предоставлять app user права чтения/записи. Без явно именованного mount Docker может создать anonymous volume, жизненный цикл которого оператор должен контролировать. Retention ограничен 31 файлом, каждый сегмент — 10 MiB; дополнительная внешняя ротация внутри этого каталога не требуется.

TLS завершается на Nginx, а Web всегда принимает внутренний HTTP и не выполняет HTTPS redirect. Nginx должен передавать `X-Forwarded-Proto: https` из сети, указанной в `ForwardedHeaders:KnownNetworks`; внешний HTTP → HTTPS redirect также принадлежит Nginx.

При запуске новой версии Web проверяет SQL Server, создаёт backup при наличии pending migrations и автоматически применяет их до Identity bootstrap и HTTP pipeline. Подробности и требования к server-side backup path находятся в [Database migrations.md](Database%20migrations.md).

## Health и остановка

Web предоставляет `/health/live` для liveness и `/health/ready` для readiness с проверкой SQL Server; `/health` — совместимый алиас readiness. Database startup имеет timeout 60 секунд и fail-fast завершает процесс при ошибке подключения, backup или migration.

## Выпуск и откат

Автоматизированный Compose-сценарий выпуска и отката не является частью первой версии. Оператор отдельно конфигурирует reverse proxy, Web и SQL Server, передаёт секреты вне репозитория, обеспечивает единственный migrating Web instance и доступ SQL Server к backup-каталогу, а rollback выполняет по правилам своей инфраструктуры.

Runtime-проверки TLS, WebSocket, log volume/permissions, миграций, backup/restore и rollback в этой задаче не выполнялись.
