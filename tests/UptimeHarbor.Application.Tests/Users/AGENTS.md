# Тесты управления пользователями

SharedAccountControlTests проверяет production Replace/ConfirmRestored и разрешение account conflict с новыми credentials; повторная подача и принятие не должны раскрывать пароль или менять сохранённый исход.

Fake account store проверяет создание, блокировку и удаление через общую administrator policy; ACL use case/repositories удалены.

Сохранять проверки текущего и последнего включённого администратора, отсутствие отдельных ролей и ресурсных назначений. SharedUserManagementTests вызывает production Pending/reducer через isolated storage/protection/credential doubles: Pending/retry/lost response, normalized login, concurrent deactivation, повторная авторизация и безопасный DTO. Не выдавать fake store за Identity runtime или SQL transaction semantics.
