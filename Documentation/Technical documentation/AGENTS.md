# Technical documentation

- Cluster operations.md владеет первым запуском/join/membership/TLS/maintenance/rollback, Cluster recovery.md — backup/rejoin/ручным аварийным выбором, Cluster control files.md — офлайн-командами и подписанными форматами. Не выдавать ручную isolation attestation за автоматическую проверку питания/firewall; commands только описывать, внешнюю приёмку учитывать отдельно.

## Назначение

Файлы этого каталога описывают фактическое техническое устройство UptimeHarbor по отдельным подсистемам.

`README.md` содержит навигацию по областям; `Libraries.md` описывает локальную DLL-поставку, её зависимости, совместимость и границы проверки.

## Правила

- Отделять текущее реализованное состояние от целевого решения следующих этапов.
- `Web application.md` владеет безопасным accepted/Pending/conflict UI/API и lifecycle; сборка не подтверждает browser acceptance или controlled enable.
- `Shared cluster storage.md` владеет моделью и SQL storage общих данных; наличие схемы не означает работающие consensus, replication, общий вход или sender.
- `Cluster runtime.md` владеет peer/mTLS, DotNext WAL/lifecycle, очередью, snapshot/replay, readiness и sender authority. `Notifications.md` описывает accepted delivery ledger/dispatcher рядом с legacy Disabled-путём. Реализованные компоненты отделять от закрытых admission/trust/activation/delivery proof; результаты изолированных API/store тестов не выдавать за выборы/crash recovery.
- Приводить имена проектов, типов и контрактов только там, где они помогают сопровождению.
- Не копировать планы реализации и бизнес-требования целиком.
- Один файл должен отвечать за одну техническую область: архитектуру, домен, persistence, Web, конфигурацию, тестирование или развёртывание.
- При изменении описанного кода обновлять соответствующий файл в этой папке в той же задаче.
- При добавлении или разделении технической области обновлять этот `AGENTS.md` и список файлов в solution.
- Deployment-документ фиксирует границы доступного Web/Dockerfile и развёртывания; deployment-шаблоны, Nginx, Docker Compose и orchestration не являются требованиями первой версии. Runtime-результаты должны отмечаться отдельно от исходной конфигурации.
