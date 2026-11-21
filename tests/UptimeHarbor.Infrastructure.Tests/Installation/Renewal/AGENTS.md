# Local voter renewal tests

ContinueCredential соединяет typed accepted сценарий с локальной установкой через memory signing journal, consensus writer и read barrier. FaultFiles прерывает immutable записи до и после production WriteOnce. Повтор открывает прежние защищённые файлы с новым InstallationLease и использует exact Bind, который уже прошёл reducer. В recovery нет signer/transport и сохраняются прежние NodeId/storage, process lease и закрытый CommonOpen. Fixture не создаёт SQL/HTTP клиенты, хост или native consensus.

LocalVoterContinuationPublicationTests проверяет initial и promoted TLS snapshots, ProcessRenewal и Enable с локальной криптографией и memory apply. Native и ordinary envelope/выбор handler вызываются через production peer transport с подставной отправкой. Новый InstallationLease, смена accepted cut и fail-closed проверяются без реального Raft, TLS handshake и сети.

B2a использует реальные временные защищённые файлы, новый InstallationLease/secret store после reopen и настоящие P-256/CSR/X.509. B1 fixture предоставляет только memory accepted images/process/majority/signing journal; БД, сеть, host и внешние процессы запрещены. FaultFiles прерывает до/после production WriteOnce; это не power-loss/fsync или SQL durability.

Матрица B2a проверяет intent/key/exact authorization/DER pin до Bind. Binding* tests B2b1 проверяют exact Bind intent/result/revision и восстановление unknown commit/lost response. B2b2 продолжает тот же fixture: material/Bind pins и manifest → installed ref → confirmation, сбои до/после записи и повтор с новым lease/store, общий Transition, отсутствие повторной записи и отказ при altered/missing/corrupt/rollback evidence. Synthetic retirement проверяет восстановление без signer или повторного Bind. Старый материал сохраняется, common gate закрыт. Runtime TLS, реальная majority/retirement orchestration и физический fsync здесь не проверяются. Секреты теста не выводить в logs/assert diagnostics.

PublicationTests открывает тот же slot с новым lease/secret store и независимым memory accepted store, проверяет fail-closed и повтор. Смена транспорта и in-flight проверяются через production peer envelope/selector и его callback подтверждения в fake primary handler с локальными сертификатами. Фактические TLS сессии и reuse сокетов остаются вне покрытия.

Для рестарта та же цепочка проверяет путь Raft до barrier, обычный peer RPC с рабочей ревизией, гонку при публикации и отказ при изменении принятого cut или credential. Native WAL reopen проверяется отдельно в Web ManagedRestartTests.

LegacyDirectVoterRenewalTests использует локальные manual CA/leaf и dedicated issuance, double статуса CRL, прежние memory journal и Bind transport. Проверять отказ CRL до эффекта, exact retry после потери ответа, принятый Bind и dedicated trust при reuse. Для реального отказа без CRL использовать локальный сертификат без AIA и CRL Distribution Points.

LocalVoterRenewalTests соединяет реальный protected store, PoP, typed signer, accepted reducer и публикацию через doubles majority, transport и journal. Reopen пересоздаёт lease, профиль, координаторы и TLS; текущий Process предоставляется тестом. Retirement для второй ротации синтетический. Reopen в этом fixture не подтверждает native ManagedRestart с незавершённой установкой.

LocalVoterRenewalRetryTests проверяет exact replay, гонку принятия и отказа, защищённую запись retry и сбои до/после неё. Проверять сохранение CSR и ключа, terminal result старого запроса и exact Bind recovery с истёкшим старым leaf. Term и majority меняются в doubles.

VoterCredentialRotationWorkerTests запускает worker через RestartableWorker с RuntimeWriteGate и управляемым таймером. Проверять закрытие CommonOpen/поколения во время прохода и отмену после accepted Bind без повторной отправки. Production inventory и admission за границей fixture.

Retirement проверять на singleton worker с новым ключом, точной границей overlap и следующим PoP, а также при reopen с сохранённым retire-intent. Для нескольких voters подставной handoff port моделирует недоступность или смену принятой проекции. Действующий typed peer путь проверяется в Web.
