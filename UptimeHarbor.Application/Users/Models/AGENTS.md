# Модели пользователей

`UserAccountRecord` — результат account store, `UserAccountDto` — прикладное представление, `AccountStoreResult` и `AccountStoreFailureKind` — независимые результаты Identity-операций.

Не возвращать Identity entities, хеши паролей и security stamps. Не смешивать store failure и Web HTTP status.
