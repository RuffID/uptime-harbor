# Модели пользователей

AccountChangeRequest несёт IDs конфликта/target/control и явное ConfirmRestored. AccountOperationDto.Outcome — безопасный AdministrationOperationDto с настоящим результатом replacement; raw account command не сериализовать.

`UserAccountRecord` — результат account store, `UserAccountDto` — прикладное представление, `AccountStoreResult` и `AccountStoreFailureKind` — независимые результаты Identity-операций.

Не возвращать Identity entities, хеши паролей и security stamps. `AccountChangeRequest` содержит явные OperationId/UserId/base revision и закрытый набор действий; пароль допустим только на входной недолговременной границе. `AccountOperationDto` раскрывает только безопасные ID/status/revision/conflict, а `UserAccountDto` — принятую ревизию. Не смешивать store failure и Web HTTP status.
