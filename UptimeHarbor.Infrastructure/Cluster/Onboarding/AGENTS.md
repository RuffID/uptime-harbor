# Адаптеры приглашений

EnrollmentTransport обслуживает enrollment и отдельный machine HTTPS ContinueCredential. ContinueCredential разрешает только шесть фиксированных POST на заранее настроенном browser origin. Сохранять системный browser TLS, адресную политику, пределы JSON/timeout и запрет redirect, proxy и cookies.

В accepted registry credential ротации проверяется по точному Bind и issuer epoch через `Renewal/VoterCredentialLeafTrust`. Dedicated issuer требует peer purpose, DER/SPKI pin и accepted revocation. Ручной CA использует цепочку/CRL на своей Direct границе.

Renewal/ проверяет исторические public DER/SPKI/exact template независимо от invitation/attempt. OnboardingAudit пишет CertificateAuthorized/Bound/Retired только по соответствующему accepted result/state; повтор известного ID не пишет второй success. Production admission новых rotation-команд остаётся закрытым до отдельной orchestration, policy overlap пока не подключена ко всем runtime boundaries.

Renewal и ContinueCredential используют прежние signing lock/UoW/journal из Renewal/. SaveVoterIssueOnceAsync сохраняет immutable voter intent, а для ContinueCredential digest отдельно связывает intent с принятой регистрацией. В прежней локальной колонке AttemptId хранится OperationId как correlation, без invitation/attempt или нового FK. Signing journal не даёт authority и не делает Bind.

Issuers/ владеет provisioning нового CA ручного кластера и административным Register через прежний Pending. EnrollmentSigner использует ленивый EnrollmentIssuerStorage: managed secrets переиспользуются, manual volumes не открываются при построении DI. Отсутствующая signing authorization оставляет выдачу закрытой. OnboardingAudit пишет IssuerRegistered/IssuerRejected только по accepted результату, без второго события на известный OperationId.

InvitationAdministration сохраняет исходную защищённую Pending до transport; retry использует прежние байты, не генерирует новый код. ILocalAccountLogin проверяет свежий пароль и локальный lockout без изменения shared credentials. После await повторяется accepted authorization.

Успех журналирует OnboardingAudit только после соответствующего accepted результата в RevisionDispatcher. Секрет, verifier, URI, CSR, certificates, credentials, payload и raw exception не журналировать. Повтор известного OperationId не даёт второе success-событие.

OnboardingProtocol различает прежний v2 и trust v3 (`3.0/3/3/3/1/6.10.0`), включая issuer state в recovery ancestors. Defaults/конфигурацию установки не менять. Production EnrollmentAuthority открывает только exact command digest capability внутри validated dedicated gateway; обычный peer batch её не имеет. Journal сохраняет исходные commands/serial/lifetime/OperationIds до consensus, signer — DER до ответа. Authenticated descriptor не содержит accounts/verifier/private refs. Local E4 не предоставляет membership/Ready.

EnrollmentSigner принимает только exact request/template/CSR, после свежего IEnrollmentSigningAuthorization внутри issuer lock; private key только AES issuer ref локальной установки. Journal lock отдельный, не удерживать его при majority/network. DER возвращать только после commit; retry того же IssueId сохраняет исходные DER/authorization binding. Issuance не является operational credential до Bind/Admit.

EnrollmentTransport сохраняет system browser TLS, fixed same-origin routes, 3/10 секунд, 64 KiB, no redirects/cookies/proxy. Nonce одноразовый, monotonic 60 секунд, bound attempt/boot/challenge/process/action/digest. Read-state/probe требуют fresh majority barrier. EnrollmentPeerProbeTransport проверяет имя TLS, custom chain и exact accepted pin/identity в обе стороны; только фиксированный read-only peer route, replay cache, current process/delegation/admission. На каждом RPC DynamicPeerTls повторяет live credential/node/issuer/revocation. Issuer signature без accepted credential не даёт authority; legacy CRL/proxy не менять.

`EnrollmentLiveTrust` и `EnrollmentPeerTrustPolicy` переиспользуют accepted `VoterCredentialPolicy.IsUsable` для expiry/revocation/retirement/overlap. Dedicated проверка предъявленного leaf требует однозначный SHA-256, exact DER и доверенный issuer. Legacy проход через accepted lookup требует отдельной проверки цепочки и существующих CRL/proxy gates.

EnrollmentDescriptorValidation закрепляет полученные IssueId/template/DER на той же попытке; действительная подпись прежнего CA не разрешает их подмену. До Bind отсутствие уже полученного DER допустимо только в CertificateAuthorized ответах read-state/resume/process-renew. Bound возвращает проверенный descriptor с прежними DER; клиент обязан сохранять возвращённое значение. Admitted descriptor требует leaf и только nonvoter без activation; расширение scopes относится к последующим протоколам.

ContinueCredential accepted-slice доступен только отдельному machine HTTPS transport по настроенному browser origin. Допуск и минимальный контракт принадлежат Renewal.

GenesisWriterPermit связывает точные InitializeCluster/BootstrapAccount payload и OperationIds; пустой accepted image не вооружает permit. После account result permit закрыт, replay читает прежний результат. Import/offline control/maintenance не расширять этим permit.
