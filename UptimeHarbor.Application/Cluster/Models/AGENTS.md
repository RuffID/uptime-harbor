# Cluster contracts

`SenderAuthorityKey.SenderNodeId` обозначает владельца попытки. Сохранять историческое имя JSON-поля `LeaderNodeId` для чтения прежних снимков и проверки digest журнала.

VoterContinuation и VoterContinuations описывают ранее принятые данные формата WAL/снимка. Маркер CredentialContinuation удерживает обычный peer ingress закрытым для такой записи.

EnrollmentTrustCommand.Rotation и OnboardingContractImage.VoterCredentialRotations — отдельный public-material accepted lifecycle voter, без invitation/private keys. Отсутствующие поля сохранять omitted. Его records/policy находятся в Onboarding/Renewal; CredentialRevision — индекс Bind, не membership version и не разрешение процесса.

AcceptedEnrollmentIssuer и EnrollmentTrustCommand — внутренние schema-3 public trust contracts, без ключей. OnboardingContracts хранит registry/GenesisInitialized; GenesisAuthorization в initial image должен поступать от проверенного локального preparation, а не из peer payload. BootChallenge и IssuanceAuthorizationIndex новых attempts не переиспользовать после смены процесса. Все отсутствующие optional поля прежних command bytes сохранять omitted, не менять старые digests добавлением null/default.

- ClusterOperationsImage/RecoveryBaseline — внутренние snapshot-контракты. Browser получает только ClusterOperationsStatus/ClusterTrustStatus без публичного сертификата, protected command и account credentials. RequestedAtUtc membership сохраняет время принятой заявки при поздней сверке admission.

- Administration* и Cluster*DiagnosticDto — явные браузерные whitelist-контракты. Account settings содержат только login/Enabled; не добавлять hash/stamps, protected/raw payload или delivery message. ConfigurationChangeRequest не принимает actor/origin/system provenance; IDs controls относятся к настоящему local Pending.

- MonitoringImage является частью того же accepted snapshot: raw observations, отдельная acceptance provenance, streams, live/history, incidents/events/intents/summaries и реальные delivery evidence. Metadata остаются по observer; diagnostic MonitoringProgress не содержит payload.
- MonitoringNodes приходит из trusted recovery admission, не из пользовательского payload; пустой/несверенный реестр не даёт внутреннюю авторизацию. Attempts/results, ActorUserId теста и retry deadline входят в accepted image; snapshot проверяет их вместе с delivery evidence. Authority из образа не является локальным разрешением sender после restart.

Диагностические записи содержат безопасные статусы/ID/подтверждения и backlog без credentials/payload. Внутренние command/image/snapshot модели могут содержать защищённые данные аккаунта; не выдавать их в UI, health, ToString или логи.
