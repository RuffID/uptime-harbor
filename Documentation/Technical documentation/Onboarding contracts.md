# Приглашения и контракты подключения

Реализованный объём этапа 02 от 2026-11-03. Экраны подключения, setup/runtime, issuer, перенос, полный Join и Telegram dispatcher остаются запланированными. Наличие этих контрактов не позволяет реально присоединить новый узел.

## Принятое состояние и секрет

[OnboardingReducer](../../UptimeHarbor.Application/Cluster/Onboarding/OnboardingReducer.cs) работает внутри прежних RevisionDispatcher → RevisionReducer → RevisionApplyEngine → projection UoW. Отдельного журнала/глобального UoW нет. Приглашение хранит типизированный статус, OperationId выдачи, accepted delegation и verifier; попытка — точный EnrollmentRequest, digest, AttemptId, исходный reserve OperationId, шаги, ProcessEpoch и отдельные сроки. Допустимо одно неиспользованное приглашение и один незавершённый Join.

Код — криптографически случайный RFC UUID v4. SHA-256 verifier разделён доменом `UptimeHarbor.Invitation.v1`, ClusterId/Generation/InvitationId; сравнение constant time. Plaintext существует только в памяти первоначальной выдачи и её первом подтверждённом HTTP-ответе. Protected Pending содержит verifier, а не код. Потеря ответа, deferred acceptance или retry возвращает состояние без восстановления plaintext; новое приглашение требует отдельного OperationId и явной замены. Замена отзывает Unused/Reserved до Admit, после Admit сохраняет прежнюю попытку и не освобождает её Join slot.

Reserve атомарно связывает NodeId/storage generation/canonical HTTPS peer URI/SPKI/CSR digest с AttemptId и exact request digest. Другой запрос/identity/key не получает эту reservation. Оригинальный OperationId возвращает прежний результат; новый reserve-запрос не продлевает истёкшее полномочие. Terminal invitation никогда не становится Unused повторно. Неизвестный результат обозначается Unconfirmed; LocalPending выдаётся только для реально сохранённой заявки.

## Время и продолжение

| Полномочие | Правило |
| --- | --- |
| Invitation | 15 минут от accepted coordinator time; при `now >= expiry` код закрыт |
| Reservation | 30 минут до первого Admit; истечение требует Pause/Continue той же identity либо отмены |
| Admission | 10 минут от accepted Admit/Renew; renew при остатке строго меньше 3 минут, возможно после expiry через будущий enrollment proof |
| Reauthentication | Не старше 5 минут и не из будущего; действующий accepted Administrator и точная credential revision |
| Process | BootId + ProcessEpoch, lease 2 минуты; новый BootId ждёт истечения старой lease и повышает epoch ровно на один |
| Inactivity | 24 часа без нового подтверждённого progress index → PauseAwaitingAdministrator; renew/poll не являются прогрессом |

OnboardingAcceptance назначает CoordinatorAtUtc/Term после majority strong barrier, проверки native term/tokens, текущего instance и accepted recovery/readiness. Произвольный CreatedAtUtc не управляет новыми сроками. Immutable request digest исключает только stamp; digest WAL/receipt покрывает полные принятые байты. Replay не читает локальные часы.

На reserve и критичных продолжениях reducer проверяет accepted enabled/role/credential revision автора delegation. Continue требует нового пароля действующего администратора и заменяет delegation revision без смены identity. Непрерывный catch-up с новым доказанным прогрессом не имеет общего ограничения 24 часа. Enrollment authority должна доказать содержательность progress/PoP; сам номер индекса от клиента такого права не даёт.

AuthorizeIssue/Bind/Admit и подготовленные process-bound Request/Authorize/ConfirmMembership используют прежние проверки и reducer состава. Admit одним write set потребляет приглашение, создаёт nonvoter и связывает credential. Legacy команды не могут обойти новые сроки/delegation. До принятой заявки состава Cancel отзывает credential, сохраняет tombstone nonvoter и освобождает попытку. При незавершённой заявке либо принятом voter возвращается MembershipPreservedRemovalRequired; нужна штатная сверка/операция удаления. Старый Activate не активирует новую onboarding identity.

## Хранение и совместимость

OnboardingImage и data contracts включены в RevisionImage; [OnboardingSnapshot](../../UptimeHarbor.Application/Cluster/Onboarding/OnboardingSnapshot.cs) проверяет состояния, binding, результаты и projection payload. `onboarding_invitations` хранит одну OnboardingProjection на приглашение с version/status constraints, canonical payload и concurrency по LogIndex. Decision/receipt/cursor сохраняются прежним projection UoW. Схема требует подготовленной [миграции](Database%20migrations.md#приглашения-2026-11-03), к БД она не применялась.

Поддерживается точная compatibility `2.0/2/2/2/1/6.10.0`: ProtocolMajor/CommandSchema/SnapshotFormat/SqlSchema = 2, SerializerVersion = 1, DotNext = 6.10.0. Старый набор v1 остаётся допустим для прежних сценариев, но новый command/snapshot отвергается до изменения состояния. Defaults и установка не менялись. Перед включением нужны согласованное обновление всех voters и отдельная подготовка схемы; mixed rollout не поддерживается.

Strict codecs ограничивают размер/глубину, отвергают duplicate/unknown/missing/null required fields, неизвестные версии и неканонические digest/UTC. Ошибки не содержат исходный JSON или inner exception. Snapshot manifest и body проверяются до install; потоковый body имеет ограничение отдельного JSON token. RecoveryBaseline сохраняет onboarding/contracts всех предыдущих generations как историю; новый active root их не наследует. Compatibility и validation обходят также Ancestors.

[ILocalOnboardingAttemptRepository](../../UptimeHarbor.Application/Abstractions/Persistence/ILocalOnboardingAttemptRepository.cs) и IOnboardingProgressUnitOfWork — подготовленные независимые локальные порты CAS/сохранения шага до сети. LocalOnboardingAttempt.Reserved удаляет protected invitation и сохраняет IDs. Их persistence adapter и управление локальными ключами принадлежат этапу 03; в 02 реализации нет.

## HTTP и production gates

`/api/admin/cluster/invitations` предоставляет Issue/Replace и защищённое чтение; подмаршруты — Revoke/Continue/Cancel. Все mutations имеют Administrator, antiforgery, лимит 4096 байт и 5 запросов в минуту на IP. Password reauthentication использует существующий ILocalAccountLogin с lockout; после await и при accepted apply права проверяются снова. Actor из JSON не принимается. DTO и ServiceResult envelope не выдают verifier/credentials/payload; ответ Code имеет no-store, не передаётся через URL/SignalR.

InvitationAdministration регистрируется в AddWebPresentation, acceptance/transport — AddClusterWeb; полный production graph включает оба модуля. Acceptance использует ClusterRaftHandle без зависимости dispatcher → runtime → sender gate. Peer batch отвергает coordinator onboarding/чужой storage/stamp до dispatch.

IEnrollmentAuthority по умолчанию реализован UnavailableEnrollmentAuthority и всегда отказывает. Подготовлены порты IEnrollmentIssuanceStore и limiter 5/origin, 30/endpoint в минуту, но enrollment endpoint, signer, nonce/grants и реальный допуск отсутствуют. Native Join/activation новой попытки закрыты до этапов 04/05. Тестовые reducer fixtures создают только записи; production registrations не используют эти doubles.

OnboardingAudit журналирует безопасные IDs и принятые переходы Issued/Replaced/Revoked/Reserved/Consumed/Expired/Paused/Continued после apply; обычный replay известного OperationId не пишет второй success. Отказы не журналируют пароль/code/verifier, URI, CSR, сертификат, payload или raw exception. Exactly-once текстовых логов при аварии между apply и записью не обещается.

## Общие форматы будущих этапов

Version 1 data contracts: GenesisAuthorization связывает install/node/storage/issuer/account-or-import digest; ImportBaselineRef — migration/genesis/cut/manifest/baseline; LegacyHistory сохраняет versioned read-only partitions с исходным payload и digest. SenderAssignment определяет generation/BotId/node/storage/revision/epoch; SenderOutage, SummaryCut и CoverageReceipt задают outage, immutable cut/frontiers и IDs частей/принятых результатов покрытия чата. OnboardingDataCodec проверяет версии, IDs, границы и digest; OnboardingContractSnapshot — ссылки между форматами. Они не исполняют импорт, не назначают sender authority и не создают успех доставки.

## Доказательства и ограничения

[Testing.md](Testing.md#приглашения-и-контракты-2026-11-03) содержит команды и результат изолированной проверки. Проверены UUID/TTL, конкурирующие команды в accepted порядке, digest/identity, lost response/другой координатор без local Pending, held restore, actor revision, process takeover, 25-часовой прогресс, cancellation/membership, strict contracts, snapshot и настоящий DotNext WAL reopen/replay, CSRF и DI.

Provider-level locking/atomicity, применение/откат миграции, fsync при аварии, реальные выборы/leader failover, TLS/enrollment/issuer/native Join/Ubuntu/browser не проверены. HTTP-тесты вызывают controller/filter напрямую, без запуска приложения/портов. Реального противоречия согласованному протоколу не обнаружено: прежний 15-минутный legacy Admit сохраняет старое поведение, новый schema-2 Admit использует отдельные 10 минут. UI остаётся запланированным.
