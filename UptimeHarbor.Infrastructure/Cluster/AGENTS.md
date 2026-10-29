# Cluster infrastructure

- AcceptedDeliveryDispatcher восстанавливает очередь из accepted image, затем использует SenderAuthorityGate и принятый BeginDelivery перед transport. Локальный outbox/claim не даёт права отправки. Все initial/retry/recovery/summary/test проходят этот путь; неизвестный proof остаётся закрытым.
- Результат транспорта сохранять локальным DeliveryResultUnitOfWork и передавать существующим revision transport, независимо от отменённого leadership token. Неопределённая попытка допускает bounded retry и поздний success; SQL capture не является accepted success. Ошибка отдельного чата не запускает resign.

- Monitoring pump передаёт bounded durable captures существующим revision transport, ACK читает только из accepted frontier. Coordinator обрабатывает historical watermark до live с ротацией/бюджетом; backlog planning требует независимо подтверждённой delivery history.
- PeerObservationCapture сохраняет аутентифицированный NodeId и только известную причину; timeout не означает remote DB fault. Snapshot/recovery сверяют monitoring rows и admitted NodeId/storage generation с ClusterMember до открытия доступа.
- Pump/coordinator, требующие Web transport/dispatcher, регистрируются в AddClusterWeb; scoped capture — вместе с persistence UoW. Не добавлять отсутствующие зависимости в самостоятельный AddClusterInfrastructure.

- DotNext 6.10.0 — единственный алгоритм Raft. WAL с CRC64, flush на append/commit и persistent configuration отделены от SQL.
- Восстановить checkpoint/snapshot до создания WAL; неизвестный checkpoint запрещает создание и старт. Не обнулять manifest/state/configuration при отказе.
- Ошибку apply держать latched до подтверждённого Restore/replay: успешный CanConnect не восстанавливает исправность проекции. Сообщения исключений reducer/provider не выпускать в DotNext/HTTP — только безопасную ошибку без inner payload.
- Состав изменять по одному участнику через DotNext после принятой авторизации Administrator; health не удаляет участников. Не регистрировать failure detector removal.
- Peer transport использует отдельные mTLS identity/limits и отключённые cookies/redirect/HTTP logging. Не доверять proxy certificate header на пользовательском listener.
- В логах только безопасные ID/reason codes; raw exceptions, URI и payload не журналировать.
- `RevisionClusterRecovery` восстанавливает принятый образ, replay SQL и сверяет старый backup до native Raft; accounts открываются только после реальной проверки committed/applied/membership и cursor/account/role/credential/tombstone. Admission/trust/activation/delivery не подменять успехом. Bootstrap writer допускает только точный digest первого аккаунта, подтверждённый replay, единственного native voter/leader и здоровую проекцию; payloadless no-op index учитывается через proven applied index.
- Snapshot передавать через bounded streams и временные файлы, проверять manifest/hash/chunks/compatibility. Переключать Current после SQL checkpoint/install; не копировать локальные сессии/ключи/Pending. Retention DotNext watermark отделён от календарной истории; compact decisions/controls/tombstones сохраняются в snapshot.
- Очередь Pending ограничена размером страницы/пакета и ёмкостью durable storage; настройки идут минутным циклом, account/delete/control — оперативно. Ошибка ответа не разрешает локальные заявки; retry не пишет повторный успех.
