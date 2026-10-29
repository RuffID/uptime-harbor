# Cluster application

- Независимые контракты consensus, принятого доверия/аккаунта и восстановления не содержат DotNext, EF или ASP.NET Core.
- Readiness, роль Raft и authority отправителя различаются. Неизвестные snapshot, accepted account state, догон или результат majority держат gate закрытым.
- Время полномочий измеряется monotonic TimeProvider; restart не восстанавливает разрешение. Частичный отказ обычной цели не является неисправностью scheduler.
- `RevisionReducer` решает команды/controls детерминированно в порядке принятия; `RevisionApplyEngine` публикует образ только после projection commit. Повтор OperationId возвращает исходный результат; SQL receipt не заменяет Raft WAL.
- `PendingCommandService` получает actor/origin из доверенного контекста, сохраняет защищённую заявку отдельно от accepted state. Старый backup сначала сверяется; подтверждение создаёт новый control, не освобождает исходный held-вариант.
- Replay не открывает чтение аккаунтов до проверки committed/applied/membership и SQL cursor. Будущие activation/trust/delivery порты остаются закрытыми.
- Приём локальных Pending сериализован singleton `PendingAdmissionGate` между scopes. Лимит 10000 незавершённых записей, 1000 зарезервировано для controls; retry прежнего ID не занимает новое место. Это локальный backpressure одного экземпляра NodeId, не consensus и не multi-process SQL locking.
