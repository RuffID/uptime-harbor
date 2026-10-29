# Cluster application

- Независимые контракты consensus, принятого доверия/аккаунта и восстановления не содержат DotNext, EF или ASP.NET Core.
- Readiness, роль Raft и authority отправителя различаются. Неизвестные snapshot, accepted account state, догон или результат majority держат gate закрытым.
- Время полномочий измеряется monotonic TimeProvider; restart не восстанавливает разрешение. Частичный отказ обычной цели не является неисправностью scheduler.
- Прикладные reducer/replay и delivery ledger подключаются в следующих этапах; заглушка не вправе подтверждать их успех.
