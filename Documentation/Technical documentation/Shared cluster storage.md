# Хранение общих данных сетки

## Реализованная граница

Этап 02 добавил независимую модель в `UptimeHarbor.Domain/Cluster`, storage-контракты Application, EF mappings, сценарные UoW и migration `20261029053354_AddSharedClusterStorage`. Эти компоненты ещё не подключены к рабочему consensus/reducer или пользовательскому входу. Новые workers, peer endpoints и UI не добавлены. Существующие однопроцессные workers не стали распределёнными.

Источник истины принятых общих данных — будущие Raft log и опубликованный snapshot. SQL хранит восстанавливаемую проекцию. `applied_journal_receipts` содержит только ID записи, позицию и digest, без payload для replay; это не SQL WAL. Term/vote и долговременный Raft WAL в SQL не реализованы. Их адаптер принадлежит этапу 03, reducer и snapshot/replay — этапу 04.

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

Внешняя авторизация, проверка base revision, генерация change set, конфликтное решение и доказательство принятого префикса остаются ответственностью будущего reducer. Это особенно важно для двух конкурентных команд отключения администратора: чистый `SharedAccount.EnsureCanDeactivate` проверяет self-action/последнего по переданному принятому count, а упорядоченное получение этого count ещё предстоит подключить в 04/05.

Отдельные `IPendingControlUnitOfWork`, `IObservationCaptureUnitOfWork`, `IDeliveryResultUnitOfWork` сохраняют только локальный сценарий. Все используют существующую scoped-сессию и EFCoreLibrary с ключом `UptimeHarborDbContext`; глобального UoW нет. Нет атомарной транзакции SQL + WAL + Telegram. SQL commit не является majority acceptance.

## Аккаунт и локальные данные

`SharedAccount` нормализует login через FormKC → Trim → invariant uppercase. SQL Server индекс использует BIN2 collation и обязательный normalized key. `ProtectedCredential` содержит hash, version и stamps; открытого пароля в модели нет. Передача таких значений и protected payload допускается только доверенным storage/peer-кодом. Фактическая защита диска/backup и peer transport этим этапом не настроена.

`ApplicationUser.ApplySharedAccount` переносит только общие поля; AccessFailedCount, LockoutEnd, cookie и ключи Data Protection не переносятся. `SharedAccount` не содержит локальных полей и является границей будущего snapshot аккаунтов; экспорт всей Identity-строки для snapshot недопустим. Общий sign-in, session epoch validation и управление аккаунтами через журнал ещё не реализованы.

Таблицы ресурсных назначений исключены из целевой модели. Их прежние Domain/Application типы временно сохранены для компиляции вызывающих use case до этапа 05. Compatibility repository читает пустые наборы, отклоняет создание назначений и не обращается к старым таблицам. Dashboard для администратора возвращает все объекты, для неадминистративного legacy-вызова — пустой набор; recipient query получает пользователей/роль одним запросом. Старый UI разрешений ещё не переработан. Legacy создание пользователя теперь назначает Administrator, legacy bootstrap создаёт только эту роль; это адаптация к схеме, а не реализация общего входа.

## Подтверждённый объём

Изолированные тесты проверяют доменные инварианты, EF metadata, операции migration, совпадение snapshot с design-time моделью, DI-разрешение storage и fake-порядок transaction/save/commit/rollback/retry. Соединение с БД не открывается. Fake-транзакция подтверждает orchestration-контракт, а не SQL Server atomicity, ограничения или блокировки. Применение Up/Down, SQL crash recovery, WAL fsync, реальный Identity sign-in на узлах и Telegram требуют отдельной проверки.
