# Peer enrollment routing

Machine HTTPS клиент и контроллер ContinueCredential используют общий `VoterContinuationWire`. Сохранять точные пути, формат и ingress marker при повторе и добавлении проверок клиента.

`accepted-slice` использует тот же machine ingress, no-store и ограничение размера. Возвращать только материал точной операции от текущего лидера. Обычный peer batch и snapshot transport не участвуют.

ContinueCredential использует отдельные machine HTTPS POST, scope runtime generation и собственные keyed nonce/replay/rate limits. Не передавать его в peer authentication, Join или DR. Актор административного чтения и подтверждения берётся из проверенного Identity application ticket общего интерфейса. LocalSetup principal и идентификатор из тела не дают полномочий.

`/issue` использует purpose-bound nonce с digest принятой регистрации и возвращает только OperationId, IssueId, DER и digest. Маршрут подключать вместе с остальным ContinueCredential на точном origin с trusted ingress marker. Status и административный API не выдают DER.

`/bind` использует отдельный nonce с digest принятой регистрации и digest сертификата из `/issue`. Принимать исходный PoP/CSR и возвращать только исходные ID, принятое решение и CredentialRevision через тот же узкий ingress.

Контроллер принимает только verified PeerEnvelope из PeerBoundaryMiddleware на прежнем isolated mTLS listener. Nonvoter, browser, pending probe и общий commands batch этих прав не получают. Ограничения: fixed routes, strict JSON, 64 KiB, 10 секунд, replay и accepted voting origin/storage.

EnrollmentPeerClient использует только named PeerClientHandler и accepted address целевого voter. Не возвращать чужой browser descriptor, не копировать peer DTO в projection. После RPC повторять native linearizable read и term/leader/membership; coordinate success подтверждать локальным accepted result с исходным digest.

HANDOFF подтверждает новый leaf и exact Bind по свежему accepted срезу. Клиент захватывает опубликованный снимок и проверяет предъявленный server leaf. На принимающей стороне требовать другого работающего voter и leaf текущего TLS-соединения. После ответа повторять проверку принятого среза. Typed `retirement-handoff` дополнительно подтверждает exact Retire после overlap. Свидетель подписывает только проверенное намерение ключом предъявленного server leaf. Лидер проверяет подпись и accepted authority. Оба порта доступны в изолированной композиции.

Remote grant требует отдельный purpose-bound peer-key ProcessProof целевого voter, а signer повторяет accepted template/process/issuer checks под своим lock. Подпись не принимает membership/activation/Enable и не разрешает CatchUp. Local process proof Bootstrap/ManagedRestart сохраняется; последующие lifecycle exchanges принадлежат 04.12.

RENEW адаптирует только typed B1 signing. На issuer сверять ID и fingerprint обоих удостоверенных middleware leaf с актуальным accepted срезом до и после подписи. Client leaf связывать с envelope и прежним credential субъекта, server leaf — с issuer и reply envelope. На caller брать адрес issuer из accepted voter, проверять exact signing evidence для server leaf на актуальном cut перед отправкой и после fresh barrier, затем проверять leaf DER. Managed и legacy Direct используют фиксированную TLS-пару для reuse. Для legacy Direct применять online CRL к ручному leaf и accepted revocation к dedicated leaf по подтверждённому credential. Typed body разрешает одну сериализацию в транспорт. Production Setup регистрирует RENEW только для рабочего managed Bootstrap Direct. Legacy proxy RENEW закрыт.

Typed rotation admission подключён в production Setup для рабочего managed Bootstrap Direct. Локальный лидер получает снимок опубликованного leaf. Удалённый использует named peer client с фактической TLS-парой и текущим leader target. Reply Authorize/Bind/Retire сверяется с локальным exact accepted решением после majority barrier. Для Retire клиент получает свежее handoff evidence по исходной команде даже при смене лидера. Отказ транспорта не подтверждает terminal Rejected. Scoped клиент и receiver используют единую регистрацию для typed Authorize/Bind/Retire.
