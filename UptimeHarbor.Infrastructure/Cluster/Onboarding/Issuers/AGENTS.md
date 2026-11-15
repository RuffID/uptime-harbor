# Регистрация локального issuer

Создание нового dedicated CA существующего ручного кластера и RegisterEnrollmentIssuer используют прежние signing SQL refs, encrypted files, Pending и dispatcher. Epoch равен исходному OperationId; повтор не генерирует новый ключ. Storage открывается только после accepted Administrator/reauthentication и проверки всех voters; legacy root/operator key не читается. Пустые Installation paths не заменять путями системного профиля или WAL.

Локальный key/ref не даёт authority. До accepted apply и нового majority/capability barrier IssuanceReady=false; signer отдельно подтверждает accepted issuance. Текущий сценарий требует локального leader; remote issuer/follower routing относится к 04.08. Не менять действующие peer CA/pins, setup profile, membership или конфигурацию установки. Orphan/неизвестный локальный commit не выбирать автоматически.
