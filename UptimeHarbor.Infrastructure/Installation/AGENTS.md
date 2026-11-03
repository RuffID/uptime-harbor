# Локальный setup и runtime

InstallationIntent=Setup обрабатывается до legacy initialization. Межпроцессный installation/storage lock удерживается весь срок процесса; порядок блокировок transition → runtime lifecycle → membership → write gate. Секреты и профиль не реплицируются. SQL-транзакции не охватывают файловые и сетевые операции.

AES-GCM использует отдельный persistent wrapping-key volume, без DPAPI. Не восстанавливать пароль из изменённого config, не активировать orphan key files. Любая неизвестная evidence оставляет common/peer/delivery gates закрытыми. Логи содержат только ID, фазу, revision/generation и закрытый reason code после durable результата.

Protected invitation — inline AES envelope в локальной attempt, после accepted reserve удаляется. Public BotId первого берётся из operator ExpectedBotId либо bounded getMe, Join не задаёт его самостоятельно. SetupPeerAddressPolicy запрещает metadata/loopback/link-local и непредусмотренные private CIDR; проверять DNS при каждом connect с IP pinning/SNI. Настоящие двусторонние TLS-пробы и accepted ProcessEpoch остаются за production authority этапа 04; локальная lease её не заменяет.
