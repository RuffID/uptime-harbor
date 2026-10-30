# Хранение общих данных сетки

## Read models и controls интерфейса (08)

Схема не расширялась. Accepted browser DTO строится из RevisionStateStore; local Pending читается существующим IPendingControlUnitOfWork страницами по 100. ProtectedPayload декодируется только внутри Application и проецируется whitelist mapping. Известный общий result перекрывает устаревшее local/held состояние при чтении, без автоматического выпуска неизвестных заявок. Edit/Cancel используют существующие controls, account resolution — тот же account pipeline. SQL остаётся проекцией; новой migration и второго журнала нет.


## Delivery ledger этапа 07

MonitoringImage включает реальные Attempts, ActorUserId тестовой доставки и NextAttemptAtUtc intent. Projection write set сохраняет DeliveryIntent/DeliveryAttempt/RecoveryEntitlement вместе с decision/receipt/cursor. Snapshot и recovery проверяют полную историю попыток, exact sets и поля projections; evidence требует соответствующего успешного accepted attempt. Authority из snapshot не восстанавливает локальное разрешение sender.

local_delivery_results сохраняет настоящий transport result до его принятия, через существующий сценарный UoW. При restart dispatcher повторяет передачу capture в тот же журнал; accepted success не отправляется повторно. FailureKind имеет закрытую категорию и необязательный RateLimited/<seconds>, без открытого ответа Telegram. Новая migration не нужна. [Поведение и неопределённое окно](Notifications.md#принятая-доставка-этапа-07).

## Проекции monitoring этапа 06

Схема 02 уже содержит capture cursors/results, observer assignments, frontiers/state, incidents/events/intents, delivery attempts/entitlements и summaries/sources. 06 подключает их к существующему RevisionWriteSetFactory/RevisionApplyEngine, без новой migration, SQL WAL или второго consensus. Raw observation неизменяем; accepted index/term/counters/historical-only находятся отдельно в MonitoringImage.

Snapshot переносит MonitoringImage и whitelist projection rows. Проверка snapshot сопоставляет их IDs/поля, включая настоящий accepted attempt для delivery evidence; Planned intent не подтверждает отправку. Recovery сверяет monitoring/configuration rows и admitted NodeId/storage generation с ClusterMember наряду с аккаунтами/cursor до открытия reader. Отсутствующая membership/delivery dependency остаётся отказом. Регистрация реестра узлов — доверенный lifecycle 09, producer attempts подключён в 07.

Captures сохраняются после ACK, события и источники сводок не удаляются по возрасту. Изолированные capture/apply/replay tests не подтверждают SQL transaction semantics или многопроцессный locking. Детали — [Monitoring.md](Monitoring.md#распределённый-путь-этапа-06) и [Testing.md](Testing.md#проверка-этапа-06).

## Реализованная граница

Этап 02 добавил независимую модель в `UptimeHarbor.Domain/Cluster`, storage-контракты Application, EF mappings, сценарные UoW и migration `20261029053354_AddSharedClusterStorage`. Этап 03 добавил [DotNext adapter, peer endpoints и lifecycle](Cluster%20runtime.md). Этап 04 подключил production reducer/replay, установку общего snapshot и durable очередь; ранний 05.1 — чтение accepted account и единую Administrator policy. 05.2–05.4 подключили accepted sign-in/session lifecycle и account commands; 06 подключил capture, observations, общие incidents/events/intents и durable состав сводок; sender подключён в 07 за закрытыми readiness dependencies. Без подтверждения независимого recovery-контракта WAL и Raft не стартуют.

Источник истины принятых общих данных — Raft log и опубликованный snapshot. SQL хранит восстанавливаемую проекцию. `applied_journal_receipts` содержит только ID записи, позицию и digest, без payload для replay; это не SQL WAL. Term/vote/log и membership сохраняются DotNext в отдельном provisioned `Cluster:StoragePath`, вне SQL. Reducer/snapshot/replay реализованы в 04; migration `20261029075201_AllowUniquenessConflictRevisions` снимает неверное сравнение competing revision с base другой сущности. К БД не применялась.

## Идентичность и версии

`ClusterIdentity` объединяет UUID сетки и generation; `JournalPosition` — её log index и term. Сравнение позиций разных сеток/generations запрещено. `CommandIdentity` различает общий UserId актёра и NodeId источника; домашнего узла аккаунта нет. Идентификаторы прикладных сущностей и операций — UUID.

`Version` серверов и контейнеров остаётся локальным optimistic concurrency token, включая результаты проверок. `ConfigRevision` и `CheckGeneration` присваиваются отдельно методом принятой конфигурации и от проверок не растут. Чат имеет ConfigRevision, привязка — BindingRevision/BindingGeneration, аккаунт — UserRevision. Позиция применения хранится отдельно в `projection_cursors`. Служебные Raft gaps допустимы только при доказанном upstream-префиксе; UoW не доказывает commit самостоятельно.

## Таблицы и защита истории

| Данные | Хранение и ограничения |
| --- | --- |
| Локальные Pending | `local_pending_operations`: неизменяемые ID/digest/payload, actor/base, retry state и намерение control; restore переводит неизвестные заявки в NeedsUserConfirmation |
| Принятые результаты и controls | `operation_decisions`, `operation_controls`: PK OperationId, один winning control на целевую операцию; terminal результат не переписывается |
| Конфликты и удаления | `shared_conflicts`: исходный, первый принятый и предложенный варианты; `shared_tombstones`: компактный ID/вид/позиция удаления |
| Применение | `projection_cursors`, `applied_journal_receipts`: concurrency cursor, уникальная позиция в generation, digest для повтора и проверки рассогласования |
| Общие аккаунты | `users`, `roles`, `user_roles`: GUID, единственная Administrator, обязательный уникальный нормализованный login, hash format, UserRevision и stamps |
| Наблюдения | `observer_assignments`, `observer_results`, `observation_frontiers`, `target_observation_states`: отдельные streams/generations/sequence, metadata каждого observer, frontier и исторические сборки |
| Локальное capture | `local_observation_cursors`, `local_captured_observations`: уникальные stream + sequence; sequence и результат сохраняются одной транзакцией |
| Инциденты и доставка | `shared_incidents`, `incident_events`, `delivery_intents`, `delivery_attempts`: стабильные event/recipient keys, максимум пять попыток, принятые успехи и неопределённые результаты |
| Локальные результаты транспорта | `local_delivery_results`: AttemptId и результат до его принятия; локальная строка не означает общий успех |
| Сводки и recovery | `delivery_summaries`, `summary_sources`, `recovery_entitlements`: неизменяемые части по chat/plan, ссылки на исходные event/intent, одно recovery без зависимости от существования binding |
| Состав и доверие | `cluster_members`, `membership_changes`, `node_chat_routes`, `peer_trust_credentials`, `cluster_trust_roots`: проекция состава, один незавершённый membership change, public trust и отдельные чаты по NodeId |
| Восстановление | `recovery_markers` — общий provenance/подтверждения; `local_node_state` — локальные storage/session generations, snapshot checkpoint и закрытые quarantine/maintenance markers |

Общие history-записи не ссылаются каскадом на удаляемые цели/чаты. События, попытки, сводки и entitlement связаны Restrict FK, поэтому очистка не может оборвать зависимости. Удаление сервера отвязывает контейнеры в той же транзакции и сохраняет их Telegram bindings. Удаление цели/чата отменяет неначатую доставку; начатый внешний запрос этим не отзывается. Удаление NodeId не удаляет пользователей; назначенный observer защищён Restrict FK до явного изменения назначения.

`HistoryRetention` разрешает удаление подробностей после календарного года от завершения только без protection flags. `HistoryRetentionQuery` читает открытый incident, незавершённые intents, Unknown attempts, summary references, recovery entitlements и исторические сборки. Это policy/query, без cleanup worker. Сначала требуется безопасно освободить связи, затем удалять завершённые подробности. Pending, компактные decisions/controls и tombstones по возрасту не удаляются; последние сохраняются на срок жизни ClusterGeneration. Незавершённая или давняя сводка защищает исходные события независимо от года.

## Транзакционные контракты

`IProjectionApplyUnitOfWork.ApplyAsync` принимает уже решённый `ProjectionWriteSet`, не пользовательскую команду. Одной транзакцией staging сохраняет account + Administrator membership, доменные изменения, decisions/controls/conflicts/tombstones, incident/events/intents/summary sources и cursor/receipt. Identity adapter не вызывает UserManager и не делает собственного SaveChanges. После ошибки выполняются rollback и очистка tracker; повтор той же позиции/digest не staging-ит изменения повторно. Несовпадающие receipt/digest, stale cursor и попытка воскресить tombstone завершаются ошибкой.

`RevisionReducer` повторно проверяет принятого active Administrator/actor revision, base, уникальность и доменные инварианты, формирует `RevisionWriteSetFactory` пакет и сохраняет конфликтные варианты. `SharedAccount.EnsureCanDeactivate` получает актуальный count из последовательного accepted image; self-disable/self-delete и последнее активное лицо защищены этим же порядком. Изменение PasswordHash требует нового SecurityStamp. Login удалённого аккаунта остаётся зарезервированным; другие удалённые сущности освобождают уникальный ключ. Endpoint/enable change требует нового CheckGeneration, metadata/связь родителя сохраняет его.

Отдельные `IPendingControlUnitOfWork`, `IObservationCaptureUnitOfWork`, `IDeliveryResultUnitOfWork` сохраняют только локальный сценарий. Все используют существующую scoped-сессию и EFCoreLibrary с ключом `UptimeHarborDbContext`; глобального UoW нет. Нет атомарной транзакции SQL + WAL + Telegram. SQL commit не является majority acceptance.

## Аккаунт и локальные данные

`SharedAccount` нормализует login через FormKC → Trim → invariant uppercase. SQL Server индекс использует BIN2 collation и обязательный normalized key. `ProtectedCredential` содержит hash, version и stamps; открытого пароля в модели нет. Передача таких значений и protected payload допускается только доверенным storage/peer-кодом. Фактическая защита диска/backup и peer transport этим этапом не настроена.

`ApplicationUser.ApplySharedAccount` переносит только общие поля; AccessFailedCount, LockoutEnd, cookie и ключи Data Protection не переносятся. `SharedAccount` не содержит локальных полей; `SnapshotSchema` экспортирует только явный whitelist общих account/role полей. Экспорт всей Identity-строки недопустим. В enabled-режиме общий sign-in и cookie validation читают accepted image; session epoch локален процессу и меняется при recovery/fault. `SharedUserManagementService` готовит credential до защищённой durable Pending и использует тот же reducer/apply UoW. Локальный login UoW сохраняет только counters/lockout, без общей journal записи.

Таблицы ресурсных назначений исключены из целевой модели. В 05.1 удалены рабочие ACL repositories/UoW, SetVisibility use case/request и endpoint. Прежние Domain-типы и пустые compatibility DTO-поля остаются до переработки UI; механизмов назначения прав нет. Dashboard требует единую administrator policy до чтения всех объектов; enabled recipient query получает активных принятых пользователей из immutable image. Старый UI разрешений ещё не переработан. Legacy создание пользователя теперь назначает Administrator, legacy bootstrap создаёт только эту роль; этот путь остаётся Disabled, а enabled writers разрешены только через общий command pipeline.

## Snapshot, Pending и retention

`RevisionStateStore` публикует принятый immutable образ после projection commit; во время recovery accounts закрыты до сверки committed/applied/membership и SQL cursor. `RevisionClusterRecovery` восстанавливает snapshot до создания native Raft, затем применяет WAL через production engine. Подтверждение recovery не открывает будущие activation/delivery зависимости.

`SnapshotProjectionUnitOfWork` экспортирует только согласованный cut и whitelist shared rows. Install валидирует accepted image/проекцию, отвергает более новый local account/cursor и отсутствие истории аккаунта, затем заменяет общие rows и checkpoint одной внешней транзакцией. Два SaveChanges имеют один commit. Pending, local_node_state identity/session epoch, lockout, Data Protection keys и секреты не заменяются. Устаревший пароль/удалённый аккаунт не подставляются поверх принятой версии.

Snapshot содержит decisions/controls/conflicts/versions/tombstones: late original после compaction остаётся решённым. Старый backup сверяет Pending по OperationId и принятому control/result до передачи; неизвестные удерживаются. Подтверждение требует активного accepted Administrator и текущей base и создаёт новый Replace control. SQL queue имеет bounded pagination/due/priority и не является журналом согласования. Singleton admission gate ограничивает локальный приём 10000 незавершённых записей, резервирует 1000 для controls; retry прежнего ID не занимает новый слот.

## Подтверждённый объём

Изолированные тесты проверяют доменные инварианты, EF metadata, операции migration, совпадение snapshot с design-time моделью, DI-разрешение storage и fake-порядок transaction/save/commit/rollback/retry. Соединение с БД не открывается. Fake-транзакция подтверждает orchestration-контракт, а не SQL Server atomicity, ограничения или блокировки. Применение Up/Down, SQL crash recovery, WAL fsync, реальный Identity sign-in на узлах и Telegram требуют отдельной проверки.
