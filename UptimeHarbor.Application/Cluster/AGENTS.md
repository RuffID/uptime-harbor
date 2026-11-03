# Cluster application

EnrollmentTrustCommands schema 3 проходят тот же dispatcher/reducer/write set; mixed payload запрещён общей RevisionCodec boundary. Request digest исключает только coordinator stamp trust/onboarding, WAL digest покрывает весь payload. Genesis/init и issuer registration не превращать в общий write permit; старые control/account paths сохраняют прежнюю авторизацию.

Publication/ConfirmRecovery в RevisionStateStore проверяют текущий RuntimeGenerationContext. Устаревший callback не публикует новый принятый образ/права. Контекст не подменяет consensus/replay proof и не вводит зависимости от hosting/EF.

Onboarding/ владеет schema-2 invitation/attempt reducer и strict contracts. Request digest исключает только coordinator stamp, WAL digest покрывает полный accepted payload; обе проверки сохранять. Legacy membership/activation не должны обходить process-bound onboarding. Общие import/sender модели в этом этапе не предоставляют runtime authority.

- Wire-коды Server / Container сохранены; Container означает ServerType.Application единой сущности MonitoredServer. RevisionDomainMapper и общий Servers write-набор обслуживают оба типа. Смена родителя/имени приложения не сбрасывает мониторинг; Server не допускает ParentId.

- Lifecycle user actions требуют текущего accepted Administrator, internal — ClusterLifecycle provenance без actor и текущего coordinator term. OperationsRevision отделена от native MembershipVersion. Held Node-команды подтверждаются Replace с текущей OperationsRevision, исходная заявка не освобождается.
- RecoveryBaseline хранит точное происхождение выбранного среза и прежнюю authority настоящих attempts. Не переписывать историю попыток и не создавать искусственный success при новой identity.

- Browser read models — только Administration* DTO через ClusterAdministrationAccess: whitelist accepted/Pending/conflict, без внутренних image/command/credentials. После async чтения Pending повторять accepted authorization; известный result перекрывает stale local/held state без освобождения неизвестной заявки.
- ClusterConfigurationCommands готовит типизированные настройки и использует существующие Pending/Replace/Cancel. Retry сохраняет исходные ID/timestamp/generation; глобальное удаление требует явного подтверждения. Held допускает безопасный Cancel; подтверждение актуальности создаёт Replace с текущей base revision. Победивший commit не откатывается.
- AdministrationMonitoring читает свежесть принятых фактов по observers, включая узлы; чтение не создаёт evaluations/incidents. Metadata и uptime разных NodeId не сводить в одно значение.

- Monitoring-команды имеют отдельный закрытый SystemSource и admitted NodeId/storage generation; не подставлять Administrator. Coordinator требует реальный локальный leader/term/catch-up, пользовательские команды сохраняют прежнюю авторизацию.
- Capture/sequence — одна локальная транзакция через существующий UoW и singleton gate. Reducer принимает raw facts/frontiers, сводит availability отдельно от coverage и создаёт incident/event/intents одним write set; clock/gap/stale evidence не доказывает outage/recovery.
- DeliveryReducer принимает RenewSender/BeginDelivery/RecordDelivery/AbandonDelivery тем же журналом, с internal provenance и admitted storage generation. DeliveryProjection восстанавливает счётчик только из настоящей непрерывной истории attempts; не создавать синтетические BeginAttempt или success.
- Recovery получателей требует настоящего accepted delivery evidence, включая поздний success после unbind. Состав summaries durable, исходные events/intents сохраняются. Известный успех и terminal exhaustion не перезапускаются; аудит выполняется после apply без повторного success при replay.

- Независимые контракты consensus, принятого доверия/аккаунта и восстановления не содержат DotNext, EF или ASP.NET Core.
- Readiness, роль Raft и authority отправителя различаются. Неизвестные snapshot, accepted account state, догон или результат majority держат gate закрытым.
- Время полномочий измеряется monotonic TimeProvider; restart не восстанавливает разрешение. Частичный отказ обычной цели не является неисправностью scheduler.
- `RevisionReducer` решает команды/controls детерминированно в порядке принятия; `RevisionApplyEngine` публикует образ только после projection commit. Повтор OperationId возвращает исходный результат; SQL receipt не заменяет Raft WAL.
- `PendingCommandService` получает actor/origin из доверенного контекста, сохраняет защищённую заявку отдельно от accepted state. Старый backup сначала сверяется; подтверждение создаёт новый control, не освобождает исходный held-вариант.
- Replay не открывает чтение аккаунтов до проверки committed/applied/membership, SQL cursor и полного shared account/role/credential/tombstone состояния. Invalidate удерживает чтение закрытым даже при следующем Publish до ConfirmRecovery с той же RecoveryVersion, с которой началась проверка. Новый Invalidate во время проверки запрещает её устаревший успех. Событие Changed отзывает локальные сессии/SignalR после apply и сбоя; исключения подписчиков не должны раскрывать payload.
- Первый BootstrapAccount разрешён reducer только по точному digest из независимого trusted recovery proof и при отсутствии истории аккаунтов. Нельзя выводить proof из пустой SQL или Mode=Bootstrap. Activation/trust/delivery открываются только production lifecycle по отдельным проверенным условиям.
- Приём локальных Pending сериализован singleton `PendingAdmissionGate` между scopes. Лимит 10000 незавершённых записей, 1000 зарезервировано для controls; retry прежнего ID не занимает новое место. Это локальный backpressure одного экземпляра NodeId, не consensus и не multi-process SQL locking.
