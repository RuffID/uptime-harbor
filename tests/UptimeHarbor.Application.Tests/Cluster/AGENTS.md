# Cluster application tests

- В сценариях bind/unbind/delete проверять Committed у самой подготовительной команды до проверки последствий: иначе rejected prerequisite может дать ложный успех проверки recovery/отсутствия отправки.

- Monitoring tests используют фактические reducer/write set: независимые observer thresholds, generation/gap/dedup, coverage отдельно от outage, metadata по узлам, node routes, стабильные events/intents и offline summaries. Delivery proof в fixtures создаётся из настоящей domain attempt; production confirmation не подделывать.

Вызывать production readiness/gate/quorum policy с управляемыми часами и контрактами принятого состояния. Fake consensus подтверждает контракт gate, а не выборы DotNext, majority commit или crash durability. Проверять также разрешённый путь, чтобы закрытый gate не давал ложного успеха набора.

Pending/reducer проверять production-кодом: trusted actor, повтор/потерянный ответ, control race, конфликты/tombstones, accepted disable и held backup. Storage doubles подтверждают сценарные границы, не SQL transactions.
