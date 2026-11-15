# Типизированная маршрутизация enrollment

Отдельные peer contracts передают public CSR/template/artifact и purpose-bound grant. Не передавать invitation plaintext, private keys, setup credentials или произвольный Sign(bytes). Назначение signer брать только из принятого issuer epoch; отсутствие owner/majority/transport закрывает выдачу без нового CA или identity.

Coordinator повторяет PoP, origin/storage, exact command/request/process и leaf binding перед узкой EnrollmentAuthority. Общий peer batch не получает capability. После удалённого commit caller ждёт локальный linearizable cut и читает собственный accepted result, не устанавливает projection из HTTP DTO.

OnlineGrantRequests сохраняет исходный grant и peer-key proof до RPC в прежнем LocalAttemptStep отдельного purpose. Startup валидирует эти steps отдельно от revision commands. Retry не меняет GrantId/template/время; expiry требует нового явного lifecycle intent. CatchUp/snapshot/emergency/maintenance здесь запрещены.
