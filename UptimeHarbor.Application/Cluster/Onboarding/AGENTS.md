# Приглашения и подготовительные контракты

Приглашения используют RevisionDispatcher → RevisionReducer → прежний projection UoW. Вторая очередь согласования и глобальный UoW запрещены. OnboardingCommand — внутренний формат, не browser request.

Сроки отсчитываются от CoordinatorAtUtc, назначенного принимающим leader. Эти поля не входят в digest исходного запроса, но входят в байты WAL/receipt. Replay не читает часы. Invitation TTL не заменяет reservation/admission/process lease.

Повтор OperationId сохраняет исходный запрос; plaintext выдаётся только первоначальному ожидающему запросу после принятия. Локальный protected code удаляется после reserve. Snapshot сохраняет terminal состояния и delegation revision.

Администратор/credential revision проверяются на accepted image при каждом критичном шаге. Продолжение после паузы требует нового reauthentication; poll не является прогрессом. Порты certificate/admission по умолчанию закрыты; тестовые данные не открывают production recovery/activation.

Общие data contracts и codec здесь не реализуют genesis/import/Telegram dispatcher. Локальные storage-контракты не входят в общий snapshot; их реализация принадлежит setup этапу.
