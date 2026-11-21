# Изолированные peer enrollment tests

Проверки machine клиента пропускают подставной HTTPS handler через штатные middleware и controller. Терять ответ после принятого действия и сверять количество записей. Проверку нового boot на границе lease отличать от accepted регистрации и возобновления установки из нового процесса.

Срез до native Start читать через `RevisionClusterRecovery` и публичный клиент с уже занятым Transition. Для replay и majority использовать отдельные memory ports. Подменять ответ или менять accepted authority на втором barrier. Проверять точный Bind, отсутствие общих данных, закрытые CommonOpen и локальную проекцию.

Штатный ContinueCredential вызывать через `ClusterPeerRuntime.StartNodeAsync` с настоящим временным snapshot/WAL/membership, защищёнными локальными файлами и подставными admission, проекцией и HTTPS. Native Start и peer HTTP запрещены fail-fast. Проверять ownership Transition, повтор после неопределённого исхода, первый карантинный снимок и остановку до следующего запроса при пропавшем историческом результате.

`VoterContinuationAdministrationTests` проверяет административный controller с trusted ticket feature, реальным MVC antiforgery-фильтром с ephemeral DP и accepted reducer в памяти. Majority и login подставляются до resolve authority. В сценариях отказа проверять отсутствие новой accepted записи и исходных пароля/digest в журнале и ошибках. Реальный cookie login и UI остаются за пределами этого набора.

Machine HTTPS Bind проверять через тот же `DefaultHttpContext` и read-only обращение к DER из memory journal. Повтор проверять и со старым, и со свежим nonce. Выдача, Bind и повтор должны иметь ровно одну запись DER и одну accepted CredentialRevision.

`VoterContinuationPerimeterTests` вызывает production middleware и контроллер через DefaultHttpContext. Подставлять majority, consensus и login до resolve authority. Локальные P-256/X.509, in-memory reducer и signing UoW подтверждают точный issue/replay через отдельный HTTPS и отказ обычного ingress на сроке leaf. Peer signing, транспорт и публикацию блокировать fail-fast. Проверка ticket через in-memory feature не подтверждает реальный cookie login или UI.

`ManagedVoterContinuationBoundStartupTests` использует production startup, временные native-файлы и подставной HTTPS. Смена InstallationLease и времени проверяет допуск после исходной lease и точного нового подтверждения. Прерывания проверяются по защищённым стадиям до первой карантинной публикации. Native Start и любой сетевой вызов блокируются заглушками.

Вызывать production client/service/controller и mTLS middleware с fake HttpMessageHandler, локальными P-256 сертификатами и in-memory native ConsensusOnlyState/ManagedRaftProbe без Start. Не использовать sockets/listeners, настоящий DNS, SQL/SQLite или второй OS-процесс.

Synthetic accepted voters и fake barriers проверяют typed routing, origin/process/template/epoch и повтор исходного intent/DER. Они не доказывают live elections/majority, TLS handshake, SQL durability или distributed file atomicity. Для native store не использовать DispatchProxy<IPersistentState> (DotNext internal setter); создавать in-memory ConsensusOnlyState.

Проверять leader/follower роли отдельно от signer owner; lost response не меняет IssueId/DER и не выполняет Bind автоматически. Общий commands batch, nonvoter, browser/pending session и arbitrary Sign(bytes) остаются закрытыми. Подпись grant без accepted record не открывает gates.

RemoteGrantFixture задаёт synthetic accepted membership и local ManagedRestart proof только для проверки RPC, не выполняет поздний Join/recovery. Проверять Process/Activation/Enable, сохранение unsigned request/proof до отправки, отказ после revocation/expiry/смены версий и отсутствие accepted consumption по одной подписи. RemoteLifecycleIntentTests отдельно воспроизводит рост cut после pre-RPC intent на настоящем Bootstrap lifecycle; synthetic remote owner/replay signer не доказывает replacement issuer.

VoterCredentialPeerFixture использует B1 signer с общим issuer lock и memory DER journal. Считать новые записи относительно журнала после Bootstrap и сверять точный IssueId. Подтверждать неизменность accepted Authorize и отсутствие Bind после peer RPC, включая потерянный ответ и replay. Отдельно проверять несовпадения credential/term/evidence и ограничение production graph режимом managed Bootstrap Direct. Смена accepted среза в double моделирует отзыв или потерю majority между проверками.

HTTP double передаёт client/server leaf в общий метод привязки, client leaf и локальный снимок после synthetic issuer publication в middleware. При overlap проверять reply и authority по снимку соединения, включая прежний снимок и отказ при отсутствующем снимке. В режиме managed pool double вызывает production callback при открытии и моделирует reuse без повторного handshake. Проверяются accepted evidence после ответа и отказ при повторной сериализации typed body. Реальные Kestrel/TLS, reuse сокетов и startup recovery остаются вне покрытия.

`VoterCredentialPeerEvidenceTests` передаёт в issuer service удостоверенные тестом ID и SHA-256 обоих leaf и проверяет allowed signing, overlap, его границу и отказы до записи в журнал. Использовать replacement credential с committed Authorize и Bind из reducer. Perimeter test повторно читает accepted trust для предъявленного dedicated leaf legacy Direct в подставном HttpContext. Реальный Kestrel `ISslStreamFeature` и online CRL остаются 07.04.

В proxy perimeter tests использовать подставной HttpContext и локальные CA/leaf. Проверять exact remote IP, результат mTLS, certificate header и accepted pin на прежнем маршруте. Проверять отказ RENEW вне Direct и при отсутствии TLS client certificate, даже при переданных forwarded headers. Реальный TCP passthrough проверяется в 07.04.

HANDOFF проверять через плановый проход, production typed client, middleware и controller с memory projection и fake TLS connection. Проверять voter вне роли лидера, отказ старому leaf на границе, exact revision и смену accepted среза или term между отправкой и ответом.

Для тестов протокола typed rotation admission подключает fixture. Проверять локального и удалённого лидера, новый Authorize, Bind и exact replay через реальный reducer и in-memory writer. Для отказа, допускающего переоформление, сверять durable OperationId/digest/index после потери ответа и смены лидера. Подмены TLS, команды, proof и ответа проверять с тем же production middleware/client и синтетическим transport. Для сквозной проверки рабочий DI-граф использует фактический supervisor, каталог и scoped rotation adapters. Оставлять только rotation worker, подставлять оба SQL-порта подписи (repository и UoW), удалять provider options и блокировать ConnectionOpening/ConnectionOpeningAsync до обращения к сети. Peer transport замыкается на in-process handler либо закрывается явным запретом для singleton.

Typed Retire проверять с singleton и другим активным voter, включая свидетеля в роли follower. Для отрицательных тестов evidence отличать отказ лидера после проверки от раннего отказа TLS/envelope. Прогон с fake transport подтверждает маршрут, overlap и exact replay с потерей ответа. Ручной ingress и native gates проверяются отдельными regression cases.
