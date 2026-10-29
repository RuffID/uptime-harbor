# Cluster application tests

Вызывать production readiness/gate/quorum policy с управляемыми часами и контрактами принятого состояния. Fake consensus подтверждает контракт gate, а не выборы DotNext, majority commit или crash durability. Проверять также разрешённый путь, чтобы закрытый gate не давал ложного успеха набора.
