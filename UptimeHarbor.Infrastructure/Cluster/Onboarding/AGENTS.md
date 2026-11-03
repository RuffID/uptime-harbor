# Адаптеры приглашений

InvitationAdministration сохраняет исходную защищённую Pending до transport; retry использует прежние байты, не генерирует новый код. ILocalAccountLogin проверяет свежий пароль и локальный lockout без изменения shared credentials. После await повторяется accepted authorization.

Успех журналирует OnboardingAudit только после соответствующего accepted результата в RevisionDispatcher. Секрет, verifier, URI, CSR, certificates, credentials, payload и raw exception не журналировать. Повтор известного OperationId не даёт второе success-событие.

OnboardingProtocol различает прежний v2 и trust v3 (`3.0/3/3/3/1/6.10.0`), включая issuer state в recovery ancestors. Defaults/конфигурацию установки не менять. Production EnrollmentAuthority открывает только exact command digest capability внутри validated dedicated gateway; обычный peer batch её не имеет. Journal сохраняет исходные commands/serial/lifetime/OperationIds до consensus, signer — DER до ответа. Authenticated descriptor не содержит accounts/verifier/private refs. Local E4 не предоставляет membership/Ready.

EnrollmentSigner принимает только exact request/template/CSR, после свежего IEnrollmentSigningAuthorization внутри issuer lock; private key только AES issuer ref локальной установки. Journal lock отдельный, не удерживать его при majority/network. DER возвращать только после commit; retry того же IssueId сохраняет исходные DER/authorization binding. Issuance не является operational credential до Bind/Admit.

EnrollmentTransport сохраняет system browser TLS, fixed same-origin routes, 3/10 секунд, 64 KiB, no redirects/cookies/proxy. Nonce одноразовый, monotonic 60 секунд, bound attempt/boot/challenge/process/action/digest. Read-state/probe требуют fresh majority barrier. EnrollmentPeerProbeTransport проверяет имя TLS, custom chain и exact accepted pin/identity в обе стороны; только фиксированный read-only peer route, replay cache, current process/delegation/admission. На каждом RPC DynamicPeerTls повторяет live credential/node/issuer/revocation. Issuer signature без accepted credential не даёт authority; legacy CRL/proxy не менять.

GenesisWriterPermit связывает точные InitializeCluster/BootstrapAccount payload и OperationIds; пустой accepted image не вооружает permit. После account result permit закрыт, replay читает прежний результат. Import/offline control/maintenance не расширять этим permit.
