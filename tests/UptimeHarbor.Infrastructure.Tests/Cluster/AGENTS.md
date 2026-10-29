# Cluster infrastructure tests

- Observation/Monitoring tests вызывают production capture UoW, pump, endpoint cycle, peer classification, coordinator, dispatcher audit и snapshot/WAL recovery на transport/time/storage doubles. Проверять также потерю ответа после commit, поздний backlog в том же term и отсутствие live до исторического watermark.
- MonitoringSnapshotRows создаёт только metadata rows без SQL connection; успешную доставку fixture подтверждает настоящими accepted attempt/result и проекцией полной истории. Граф тестового accepted proof не переносить в production registrations.
- DeliveryHarness вызывает production gate/consensus/dispatcher/reducer с локальными storage/transport/time doubles. Проверять очередь без legacy outbox, окно внешнего success до принятия результата, поздний success/unbind, независимость чатов и follower Pending. Fake sender не доказывает exactly-once, fake UoW — SQL locking/atomicity.

Тестировать реальный guard, options, WAL/state machine и transport adapters. WAL открывается только во временном каталоге; закрытие/повторное открытие не доказывает fsync при аварийном завершении. Не запускать Raft nodes, HTTP, SQL или Telegram. Protocol substitute проверяет вызов DotNext API, без собственной реализации выборов/majority.

Snapshot/import/compaction и production replay проверять с фактическими файлами и DotNext WAL; SQL projection double не считать доказательством provider atomicity. Pump/batch используют изолированные transport/time/storage; неверный пакет не разрешает Pending и не сдвигает cursor.
