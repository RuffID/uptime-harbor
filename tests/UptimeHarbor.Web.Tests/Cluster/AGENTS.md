# Изолированные проверки cluster hosting

ManagedBootstrap/ManagedRestart tests используют production permit/recovery/signers/validators/lifecycle и реальный локальный WAL; SQL/native majority и membership file — doubles, native Start/elections не выполняются. EnrollmentClient/Gateway tests соединяют production client/server через fake handlers без listeners; TLS policy проверяется локальной криптографией, не handshake. Проверять обе peer-пробы, lost reserve/IssueId/DER, новый BootChallenge/ProcessEpoch/lease, file intent/crash/orphans и закрытый CommonOpen второго. Не запускать реальную сеть или host ради этих тестов.

ManagedRestartTests проверяет ранний отказ для режима, отличного от точного Restart: admission/TLS/native store остаются закрытыми, профиль и файлы не меняются; отсутствующий seal не должен читаться. Успешный Restart отдельно проверяет TLS, карантин и новый ProcessEpoch/Enable.

ManagedBootstrapRecoveryTests теряет ответ после каждого из семи committed шагов genesis/account/process/activation/Enable. ManagedRaftProbe считает native calls и позволяет потерять конкретный ответ; ManagedBootstrapStore предоставляет управляемые границы до/после подставного commit, не моделирует SQL rollback/locking. Common UI проверяется прямым вызовом SetupBoundaryMiddleware с явно заданными method/scheme/Host, без host/browser. Отдельно проверять commit отзыва setup до публикации, неизвестный ответ commit, отказ Ready и новый process после sealed restart. Неполный первый старт без seal должен безопасно отказывать без нового genesis/account; это не доказательство его автоматического resume.

ManagedInvitationTests проверяет schema3 после Bootstrap/Restart, accepted common actor и отказ setup identity/неверному паролю. Потерянный ответ не возвращает code повторно; для нового кода нужна явная замена. Повтор известного OperationId не должен делать новый native effect.

ManagedStorageSealTests проверяет исходные state/checkpoint до native open, stale WAL, монотонность cut, сбои до SQL checkpoint/после него/после seal и параллельный обычный apply со snapshot. ManagedCheckpointProjection хранит head независимо от accepted state и предоставляет управляемые checkpoint boundaries; согласование проверять через TaskCompletionSource без случайных задержек. Локальный WAL и SQL double не подтверждают power-loss/fsync или provider locking.

SetupSecurity/SetupComposition/ManagedSupervisor tests проверяют production cookie/antiforgery/boundaries, полный DI без Start/WAL и реальный supervisor с подставным catalog loops. LegacySetupSelectionTests запрещает разрешение cluster Pending в обычном Disabled test-send. Это не проверка настоящего cutover/TLS/второго процесса; doubles authority/admission не регистрируются production graph.

InvitationHttpFixture использует настоящие Pending/dispatcher/reducer/apply и закрывает advanced enrollment. InvitationSecurityTests вызывает настоящий antiforgery filter с ephemeral DP: получение токена и POST используют разные HttpContext, а выполняемый filter включён в context.Filters. Полный graph проверять без Start/сети; не возвращать runtime dependency из acceptance в dispatcher.

- Lifecycle HTTP tests проверяют actual accepted actor, protected Pending/held Replace и 202 без запуска host. Peer boundary отвергает internal lifecycle/foreign storage до dispatch. PersistentMembershipTests вызывает production ClusterJoinMembership с настоящим временным native store, исключая joining nonvoter и повторную запись; это не live join/elections.

- Administration HTTP/security/diagnostic tests вызывают production API/filter/read models с реальными MVC options и изолированными хранилищем/health. Проверять 202 как local queue, closed auth, safe conflict variants, stale Telegram и различие peer outage/локальная БД/follower; не называть это browser или peer-network acceptance.

- Monitoring revision boundary проверяет совпадение storage generation с authenticated peer до dispatch; composition tests должны разрешать новые worker/pump/coordinator без host/WAL/HTTP, сохраняя закрытые gates.
- В итоговом Web graph разрешается DeliverySenderConsensus; самостоятельный Infrastructure module сохраняет UnavailableSenderConsensus. Проверять отсутствие DI цикла writer/runtime/gate и сохранение закрытых readiness dependencies.

- Вызывать middleware и DI напрямую, без host/start, слушающих портов и реальных запросов.
- Persistent configuration DotNext проверять в отдельном временном каталоге. Не запускать Raft-узлы и не называть store/API tests проверкой выборов или crash recovery.
- AccountSessionTests вызывает production apply/reducer/session и fake HubCallerContext: password/stamp/disable/delete/fault должны отзывать защищённую подписку, обычный offline/recheck — сохранять её. Это не WebSocket/browser acceptance.
- Controller batch проверять до consensus dispatch, включая malformed tail и отсутствие verified peer item. Cluster composition должна разрешать production recovery с закрытым admission, не создавать native state в DI validation.
