# Cluster application tests

- В сценариях bind/unbind/delete проверять Committed у самой подготовительной команды до проверки последствий: иначе rejected prerequisite может дать ложный успех проверки recovery/отсутствия отправки.

- Monitoring/delivery tests используют фактические reducer/write set: независимые observer thresholds, generation/gap/dedup, coverage отдельно от outage, metadata по узлам, node routes, стабильные events/intents и offline summaries. Delivery proof в fixtures создаётся через реальные BeginDelivery/RecordDelivery; production confirmation не подделывать.
- Retry/rebind/replay не должны возобновлять известный успех или terminal exhaustion. Проверять поздний success старой попытки после failover, recovery только уведомлённым чатам и согласованность projection/snapshot полной истории attempts.

Вызывать production readiness/gate/quorum policy с управляемыми часами и контрактами принятого состояния. Fake consensus подтверждает контракт gate, а не выборы DotNext, majority commit или crash durability. Проверять также разрешённый путь, чтобы закрытый gate не давал ложного успеха набора.

Pending/reducer проверять production-кодом: trusted actor, повтор/потерянный ответ, control race, конфликты/tombstones, accepted disable и held backup. Storage doubles подтверждают сценарные границы, не SQL transactions.
