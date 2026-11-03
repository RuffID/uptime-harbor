# 03б. Перенос и переключение первого сервера

Статус: `not-started`. Зависимости: [02](../02-invitations-and-contracts/main.md)–[04](../04-enrollment-and-trust/main.md), [03а](../03a-standalone-migration-preflight/main.md).

Граница после 03: installation/runtime-порты, supervisor и поколения подготовлены, но переход непустого standalone не разрешён. Использовать их после manifest/preflight 03а и настоящей authority 04; setup identity не заменяет действующего legacy-admin.

## Цель

Перевести действующий первый сервер в первый кластерный узел с сохранением пользователей, объектов, чатов, привязок и истории, без перезапуска всего приложения.

## Работы

1. Запускать переход только действующим локальным администратором с CSRF-защитой, актуальным preflight и одним долговременным migration ID. Проверить резервную копию/план возврата в разрешённом внешнем сценарии; наличие этого плана само по себе не разрешает backup или SQL.
2. Войти в явную короткую паузу: закрыть конкурирующие изменения, остановить standalone-проверки и старую доставку, дождаться известного состояния in-flight операций и закрепить контрольный срез. Web-host и безопасная страница прогресса продолжают работать.
3. Сохранить исходный набор и сформировать первоначальную проекцию/manifest/accepted baseline по типизированным контрактам этапа 03а. Не выполнять произвольный SQL из HTTP, не очищать данные и не запускать offline CLI. Фиксировать долговременный результат каждого шага до следующего внешнего эффекта.
4. Сверить пользователей и возможность входа, IDs/настройки/связи объектов, чаты/bindings, историю наблюдений/инцидентов/доставок и состояние очереди. Сохранить исходное происхождение истории и защиту от повторной отправки; проверять не только число строк, но и значимые значения/связи.
5. Подготовить сертификаты/identity и первый кластерный состав штатными online-контрактами. Переключить runtime по этапу 03 без перезапуска Web-host; действующие проверки первого узла сохранить с явным назначением. Токен бота остаётся локальным, отправка через старый worker не возобновляется. До готовности отдельной sender authority этапа 05а доставка явно ожидает.
6. Только после сверки и readiness разрешить общий вход и выдачу приглашений. Второй и третий подключаются обычным Join; их локальные setup-аккаунты не сливаются с перенесёнными пользователями.
7. На обрыве возобновлять исходную миграцию с проверкой фактически записанного состояния. До точки переключения возврат возможен только к проверенному источнику; после начала accepted-записей не включать автоматически старый standalone и не допускать двух источников записи. Отказ оставляет данные и безопасный статус для диагностики.
8. Логировать начало, контрольный срез, результат сверки, переключение, отказ и завершение после durable результата. Использовать migration ID, actor ID и счётчики без секретов и содержимого истории; не писать success при откате/неизвестном исходе.

## Затрагиваемые части

### Контракт, разработанный этапом 01

Реализовать [M0–M3 и ImportStandaloneBaseline](../technical-design.md#migration), [runtime gate](../technical-design.md#runtime) и параметры pause/drain [30/10 секунд](../technical-design.md#parameters). Source freeze и backup/restore evidence — перед M2. Отдельный genesis-only import не ослабляет nonempty guard Join. ClusterIntent сохраняется до native эффекта; unknown commit закрывает возврат. До M2 старый единственный writer можно открыть только после доказательства отсутствия commit, после M2 автоматического standalone rollback нет. Первый общий account set строго из manifest, без добавления BootstrapAccount.

Сверить hashes/values/links/вход/queue provenance, сохранить legacy archive в baseline/snapshot, прежние observer assignments первого и явное sender assignment с локальным token. Baseline готовится как sealed provisional genesis blob/shadow state, в Raft идёт только bounded marker с digest; reducer не читает файл и не создаёт JournalPosition(0). До приглашений нужен обычный snapshot ≥import index со всем baseline. Проверить baseline >256 KiB, потерю blob и crash между marker/SQL/snapshot. Production доставка закрыта до 05а, перенос/CanWork от неё не зависят. Проверить M-boundaries, budget/drain failure, disabled admin, unknown sends и старый worker после commit. События — только после durable результата; подробнее [матрица](../technical-design.md#verification).

Application migration use cases/UoW contracts, Infrastructure persistence/identity/history adapters, Web runtime lifecycle и migration status. Актуализировать ближайшие AGENTS.md, Shared cluster storage, Cluster runtime, Cluster operations, Notifications и Testing по фактической реализации.

## Проверки и приёмка

- Данные и возможность входа сохранены, прежняя история доступна, известные доставки не превращены в новые сообщения.
- Сбой между каждым durable шагом, конкурентное подтверждение и повтор запроса не создают второй baseline или потерю источника.
- При ошибке сверки выдача приглашений и рабочие gates закрыты; исходные данные не удаляются и старая запись не запускается параллельно.
- Успешный переход не перезапускает Web-host; после внешнего рестарта продолжается тот же migration ID.
- Изолированные проверки не подменяют перенос копии реальной непустой БД и восстановление backup. Эти обязательные внешние проверки входят в этап 07 и требуют отдельного разрешения.

## Результат

Пока не выполнен. Подготовка механизма переноса не подтверждает сохранность реальной пользовательской БД; приёмка и рабочий ввод фиксируются отдельно.

## Подэтапы

Каждый файл ниже — отдельная задача. Общие требования выше сохраняются; файл подэтапа задаёт узкую границу исполнения и проверяемый результат.

| Подэтап | Зависимости | Статус |
| --- | --- | --- |
| [03b.01. Запуск единственной миграции](03b.01-migration-start.md) | [этап 02](../02-invitations-and-contracts/main.md), [этап 03](../03-initial-setup/main.md), [этап 03a](../03a-standalone-migration-preflight/main.md), [этап 04](../04-enrollment-and-trust/main.md) | `not-started` |
| [03b.02. Пауза записи и drain](03b.02-freeze-and-drain.md) | [03b.01](03b.01-migration-start.md) | `not-started` |
| [03b.03. Финальная delta и sealed genesis blob](03b.03-final-delta-and-genesis-blob.md) | [03b.02](03b.02-freeze-and-drain.md) | `not-started` |
| [03b.04. Принятие baseline и запрет старого writer](03b.04-bounded-import-marker.md) | [03b.03](03b.03-final-delta-and-genesis-blob.md) | `not-started` |
| [03b.05. Сверка данных и прежнего входа](03b.05-data-reconciliation.md) | [03b.04](03b.04-bounded-import-marker.md) | `not-started` |
| [03b.06. Identity и включение первого runtime](03b.06-singleton-runtime.md) | [03b.05](03b.05-data-reconciliation.md) | `not-started` |
| [03b.07. Возобновление и границы возврата](03b.07-crash-resume-and-return.md) | [03b.01](03b.01-migration-start.md), [03b.02](03b.02-freeze-and-drain.md), [03b.04](03b.04-bounded-import-marker.md) | `not-started` |
| [03b.08. Полный snapshot и открытие приглашений](03b.08-accepted-snapshot-and-invitations.md) | [03b.06](03b.06-singleton-runtime.md), [03b.07](03b.07-crash-resume-and-return.md) | `not-started` |
| [03b.09. Изолированная матрица и документация переноса](03b.09-isolated-evidence-and-docs.md) | [03b.01](03b.01-migration-start.md), [03b.02](03b.02-freeze-and-drain.md), [03b.03](03b.03-final-delta-and-genesis-blob.md), [03b.04](03b.04-bounded-import-marker.md), [03b.05](03b.05-data-reconciliation.md), [03b.06](03b.06-singleton-runtime.md), [03b.07](03b.07-crash-resume-and-return.md), [03b.08](03b.08-accepted-snapshot-and-invitations.md) | `not-started` |

Порядок определяется зависимостями в таблице. Внешние предпосылки и отдельные разрешения из общего плана сохраняются; переход к следующей задаче не происходит автоматически.
