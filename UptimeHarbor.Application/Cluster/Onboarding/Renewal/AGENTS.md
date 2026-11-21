# Accepted voter credential rotation

ContinueCredential использует отдельные Request/Authorize/Register/Bind в accepted журнале VoterContinuations. Контекст фиксирует запрос, issuer revisions, аудиторию, term/membership/recovery, кандидата и предшественника lease. Reducer проверяет действующее право администратора и границы lease на accepted timestamp. Регистрация резервирует process epoch без изменения membership/credential. Bind добавляет credential с ревизией по accepted index и нулевым оставшимся overlap. Snapshot сверяет цепочку решений, lease, rotation и точный credential, включая recovery history.

Передача в штатный Process проверяет exact accepted Bind и регистрацию на границе `RenewOnlineProcess`. В подписанном ProcessRenewal `SnapshotDigest` содержит digest этого Bind. Только действующая continuation lease того же boot, epoch и recovery может быть заменена таким Process. Новый boot проходит отдельный принятый допуск после окончания прежней lease.

Запрос восстановления после принятого Bind содержит `BoundSource` в подписываемом intent. Его собственные Request/Authorize/Register связываются с исходным RequestId, digest Bind и CredentialRevision. Проверять неизменный issuance template и exact expired predecessor из цепочки регистраций этой операции. Смена boot не изменяет исходный Bind, выдачу или его lease. Команда Bind с `BoundSource` недопустима. Snapshot проверяет исторический источник независимо от действующей lease.

Состояние живёт в OnboardingContracts.VoterCredentialRotations прежнего WAL/snapshot, независимо от invitation/attempt. До Bind нет OperationalCredential; CredentialRevision равна accepted Bind index (начальное credential — revision 0). RenewalId равен IssueId нового credential.

Reducer не читает часы/I/O: только CoordinatorAtUtc/Term. Обычный Renew требует живой старый credential, окно ≤7 дней, новый SPKI/CSR и точный 30-дневный template. Bind не продлевает исходный срок, overlap закрывается при min(Bind+24ч, expiry старого) даже без Retire. Retire сохраняет историю отдельно от compromise revocation. Один незавершённый переход на NodeId; следующий начинается с текущего credential/revision после retirement.

Точки доверия переиспользуют `VoterCredentialPolicy.IsUsable` для принятого среза. У старого credential после Bind должны совпасть начало и вычисленная граница overlap. Отсутствие границы, выход за окно, accepted revocation или retirement закрывают доверие.

Эти records и digest не доказывают PoP/CSR/majority. Production RevisionDispatcher закрывает новые команды до отдельной orchestration/authority проверки; не снимать запрет только по SystemSource/stamp. ContinueCredential требует отдельного accepted admin proof и этим Renew не разрешён. Snapshot проверяет ссылки на committed decisions и цепочку revision; Infrastructure дополнительно проверяет DER/template.

Authority contracts отделяют immutable VoterCredentialRequest/OperationId от короткого process-bound PoP и typed signing request с exact AuthorizationIndex. VoterCredentialAuthorizationCommand создаёт только исходный envelope без coordinator stamp; это не dispatch capability. Реализации криптографии/barrier/transport находятся вне Application. Подпись не выполняет Bind/Ready, private key и CSR bytes не входят в accepted state.
