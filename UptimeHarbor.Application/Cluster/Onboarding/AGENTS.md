# Приглашения и подготовительные контракты

Приглашения используют RevisionDispatcher → RevisionReducer → прежний projection UoW. Вторая очередь согласования и глобальный UoW запрещены. OnboardingCommand — внутренний формат, не browser request.

Сроки отсчитываются от CoordinatorAtUtc, назначенного принимающим leader. Эти поля не входят в digest исходного запроса, но входят в байты WAL/receipt. Replay не читает часы. Invitation TTL не заменяет reservation/admission/process lease.

Повтор OperationId сохраняет исходный запрос; plaintext выдаётся только первоначальному ожидающему запросу после принятия. Локальный protected code удаляется после reserve. Snapshot сохраняет terminal состояния и delegation revision.

Администратор/credential revision проверяются на accepted image при каждом критичном шаге. Продолжение после паузы требует нового reauthentication; poll не является прогрессом. Порты certificate/admission по умолчанию закрыты; тестовые данные не открывают production recovery/activation.

Schema-3 EnrollmentTrust commands используют прежний reducer/projection и отдельный bounded payload. InitializeCluster требует точного GenesisAuthorization из trusted initial image, создаёт Node до credential и не принимает самоподписанное входящее разрешение. Issuer retirement/revocation сохраняют public anchor/history. Snapshot сверяет issuer owner/revision/digests и projection rows. Schema-2 WAL сохраняет прежние semantics; новые online команды требуют schema 3.

Schema-3 попытка сохраняет BootChallenge и IssuanceAuthorizationIndex. Resume ждёт предыдущую lease и меняет boot/challenge/epoch; критичные продолжения проверяют issuer/delegation/credential revocation. CSR extension policy и private signing принадлежат Infrastructure; record/digest сами authority не создают. Import/Telegram dispatcher здесь не реализованы. Локальные signing ports не входят в общий snapshot.
