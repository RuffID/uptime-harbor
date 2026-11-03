# Сборка и развёртывание

Этап 04 ещё не образует готовый rollout. Production новый первый Bootstrap и trust/admission E1–E4 подключены; полный Join, перенос, sender и мастер закрыты до своих этапов. AddEnrollmentTrust сгенерирована с отдельного разрешения без подключения/применения к БД. Перед включением отдельно подготовить все voters/schema/Direct peer endpoint. Изолированные crypto/WAL/files/HTTP doubles не проверяют Ubuntu/TLS/ACL/fsync, SQL или переключение окружения. [Реализованность](Onboarding%20contracts.md#промежуточная-граница-этапа-04-2026-11-03).

## Подготовленный setup (этап 03)

Новый путь требует отдельного Direct/TLS-passthrough peer listener, подготовленного до Build, browser HTTPS, совместимой SQL schema и persistent state/secret/keys/storage volumes с ограниченными правами. Wrapping key создаётся приложением автоматически; дополнительный установочный секрет и Windows DPAPI не требуются. Пути/схема/listeners/ACL не исправляются HTTP-запросом, Nginx из приложения не управляется. Конфигурация установки, Docker и окружение в этой задаче не изменялись.

Supervisor выполняет подготовленные переходы без перезапуска Web-host. Production issuer/activation нового первого и отдельный enrollment client/server подключены; двойная TLS-проба проходит фиксированные peer endpoints без команд. Managed storage seal и snapshots входят в локальную согласованную сохранность; restore/cloning этим не разрешены. Ubuntu/TLS/ACL/fsync, второй OS-процесс и переключение живого окружения не проверены. [Inventory и границы](Local%20installation%20setup.md).

## Эксплуатация распределённого режима

Точные процедуры, предпосылки, условия остановки и границы разрешений: [первый узел/join/membership/TLS/maintenance/rollback](Cluster%20operations.md), [backup/rejoin/ручная авария](Cluster%20recovery.md), [офлайн-команды и control files](Cluster%20control%20files.md). Production dependencies реализованы, default Disabled сохранён; текущая установка не включалась. Интернет-связь допускается по доменным HTTPS-адресам без обязательного VPN. Выпуск/renew сертификатов и локальные секреты принадлежат окружению, а ручное подтверждение специалиста не является автоматической проверкой питания/firewall.

## Состояние реализации

В Web-проекте есть multi-stage Dockerfile, который восстанавливает и собирает Domain, Application, Infrastructure и Web, копирует локальные DLL из `libs` и запускает `UptimeHarbor.Web.dll` на ASP.NET runtime image. Deployment-шаблоны, Nginx-конфигурация, Docker Compose и orchestration не входят в требования первой версии и в репозитории не поставляются.

## Границы первой версии

Первая версия не фиксирует целевую схему reverse proxy, контейнеров или orchestration. Выбор средств запуска Web и SQL Server, сетевой изоляции и хранения данных остаётся ответственностью окружения.

## Конфигурация и данные

Production-конфигурация и секреты должны задаваться средствами целевого окружения вне репозитория. Данные SQL Server и Identity Data Protection keys (таблица `data_protection_keys`) должны сохраняться средствами этого окружения. Для сетки сохранять также локальные manifest, Raft WAL/term/vote/native membership, snapshots, control maintenance marker и собственные Pending/keyrings по [правилам backup](Cluster%20recovery.md#что-входит-в-backup); SQL остаётся проекцией log + snapshot.

Serilog пишет человекочитаемые логи в console и rolling-файлы в `/app/Logs`. Dockerfile создаёт каталог с владельцем `$APP_UID`, режимом `0700` и объявляет его volume. Для контролируемого хранения следует подключить named volume или bind mount; bind mount должен предоставлять app user права чтения/записи. Без явно именованного mount Docker может создать anonymous volume, жизненный цикл которого оператор должен контролировать. Retention ограничен 31 файлом, каждый сегмент — 10 MiB; дополнительная внешняя ротация внутри этого каталога не требуется.

Browser TLS может завершаться на Nginx с внутренним HTTP; приложение не выполняет HTTPS redirect. Nginx передаёт `X-Forwarded-Proto: https` только из доверенной сети `ForwardedHeaders:KnownNetworks`; внешний redirect принадлежит окружению. Enabled peer listener отдельный: Direct использует HTTPS/mTLS непосредственно в Kestrel, NginxIsolatedHttp — изолированный внутренний HTTP с проверяемыми certificate headers. Не переносить настройки browser proxy на peer ingress без его отдельной проверки.

Только в Disabled startup Web проверяет SQL Server, создаёт backup при наличии pending migrations и автоматически применяет их до legacy Identity bootstrap и HTTP pipeline. Database startup имеет timeout 60 секунд и fail-fast завершает процесс при ошибке подключения, backup или migration. В Bootstrap/Join/Restart этот путь пропущен: схема подготавливается отдельно разрешённой операцией, ClusterDatabaseProbe только проверяет connectivity и отсутствие pending migrations. Аккаунты создаёт/восстанавливает controlled lifecycle. Подробности и требования к server-side backup path находятся в [Database migrations.md](Database%20migrations.md).

## Выпуск и откат

Автоматизированный Compose-сценарий выпуска и отката не является частью первой версии. Оператор отдельно конфигурирует reverse proxy, Web и SQL Server, передаёт секреты вне репозитория, обеспечивает единственную операцию подготовки/обновления схемы и доступ SQL Server к backup-каталогу, а cluster rollback выполняет по [правилам обслуживания](Cluster%20operations.md#обслуживание-обновление-и-rollback).

Runtime-проверки TLS, WebSocket, log volume/permissions, миграций, backup/restore и rollback в этой задаче не выполнялись.
