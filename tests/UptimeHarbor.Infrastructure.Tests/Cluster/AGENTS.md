# Cluster infrastructure tests

OnboardingStorage/BoundaryTests проверяет migration metadata с закрытым connection, exact snapshot/projection, compatibility включая ancestors, limiter и настоящий DotNext WAL reopen/replay. IEnrollmentAuthority остаётся false. Не запускать весь Infrastructure: вне Cluster есть provider-тесты с БД.

- SnapshotMonitoringCompatibilityTests проверяет точное преобразование прежних monitoring rows с сохранением ID/parent/state и отказом на неизвестные поля; metadata-контекст соединение не открывает.

- Control/admission tests используют настоящую ECDSA/CA/client certificate и локальный DP keyring во временных каталогах. Проверять stale/revoked proofs, новый process challenge и restart/maintenance без автоматического enable.
- Emergency tests выбирают snapshot с настоящими accepted attempts/results, проверяют ручной отказ/изоляцию/пароль/lag и новую quarantined identity. Новый WAL и term floor проверяются без Raft Start/сети; это не физическое fencing, SQL locking или crash/fsync.
- RevisionRecoveryTests.Projection — общий изолированный projection double для replay и emergency WAL tests; его проверки cursor не доказывают SQL semantics.

- Observation/Monitoring tests вызывают production capture UoW, pump, endpoint cycle, peer classification, coordinator, dispatcher audit и snapshot/WAL recovery на transport/time/storage doubles. Проверять также потерю ответа после commit, поздний backlog в том же term и отсутствие live до исторического watermark.
- MonitoringSnapshotRows создаёт только metadata rows без SQL connection; успешную доставку fixture подтверждает настоящими accepted attempt/result и проекцией полной истории. Граф тестового accepted proof не переносить в production registrations.
- DeliveryHarness вызывает production gate/consensus/dispatcher/reducer с локальными storage/transport/time doubles. Проверять очередь без legacy outbox, окно внешнего success до принятия результата, поздний success/unbind, независимость чатов и follower Pending. Fake sender не доказывает exactly-once, fake UoW — SQL locking/atomicity.

Тестировать реальный guard, options, WAL/state machine и transport adapters. WAL открывается только во временном каталоге; закрытие/повторное открытие не доказывает fsync при аварийном завершении. Не запускать Raft nodes, HTTP, SQL или Telegram. Protocol substitute проверяет вызов DotNext API, без собственной реализации выборов/majority.

Snapshot/import/compaction и production replay проверять с фактическими файлами и DotNext WAL; SQL projection double не считать доказательством provider atomicity. Pump/batch используют изолированные transport/time/storage; неверный пакет не разрешает Pending и не сдвигает cursor.
