# Типизированная маршрутизация enrollment

Отдельные peer contracts передают public CSR/template/artifact и purpose-bound grant. Не передавать invitation plaintext, private keys, setup credentials или произвольный Sign(bytes). Назначение signer брать только из принятого issuer epoch; отсутствие owner/majority/transport закрывает выдачу без нового CA или identity.

Coordinator повторяет PoP, origin/storage, exact command/request/process и leaf binding перед узкой EnrollmentAuthority. Общий peer batch не получает capability. После удалённого commit caller ждёт локальный linearizable cut и читает собственный accepted result, не устанавливает projection из HTTP DTO.

OnlineGrantRequests сохраняет исходный grant и peer-key proof до RPC в прежнем LocalAttemptStep отдельного purpose. Startup валидирует эти steps отдельно от revision commands. Retry не меняет GrantId/template/время; expiry требует нового явного lifecycle intent. CatchUp/snapshot/emergency/maintenance здесь запрещены.

У Bootstrap proof для rotated credential подписывает только ключ рабочей публикации текущей CredentialRevision, в том числе после ManagedRestart. Исторические steps сохраняют привязку к исходному Bootstrap. Перед повтором запроса обязательны совпадение с рабочим credential, прежний source gate и проверка подписи по accepted leaf.

Whitelist RENEW предназначен только для typed B1 signer по exact accepted Authorize. Путь использует общие пределы enrollment peer и регистрируется в рабочей композиции managed Bootstrap Direct. Он не добавляет разрешение общему revision batch.

HANDOFF использует тот же Direct-периметр для взаимной проверки после Bind и подтверждает только exact CredentialRevision и предъявленные сертификаты.

Четыре фиксированных маршрута rotation передают nonce, Authorize, Bind и Retire для отдельного приёма managed Bootstrap Direct. `retirement-handoff` использует Direct-периметр HANDOFF и передаёт свидетельство другого voter для exact Retire. Обычный coordinator и batch не предоставляют права ротации. Повтор решения и окончательный отказ сверяются по локальному accepted журналу.
