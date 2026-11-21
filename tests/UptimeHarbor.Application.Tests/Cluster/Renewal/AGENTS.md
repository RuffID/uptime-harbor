# Accepted renewal tests

Проверять прежний RevisionReducer/RevisionCodec/write set/snapshot на in-memory accepted images без БД/HTTP/хоста. Fake public bytes проверяют структуру и SHA-256, но не X.509; реальные DER проверяются Infrastructure Renewal tests.

Успех предпосылок Authorize/Bind/Retire проверять явно. Проверки replay должны сохранять исходный intent/DER/revision/deadline, не только Outcome. Отдельно подтверждать запрет новых команд и выдачи известных решений ротации через общий dispatcher.
