# Renewal crypto tests

Manual вариант authority fixture использует локальные CA/leaf и подставляет только проверку состояния CRL поверх реальной custom-root цепочки, без AIA и CRL загрузок. Он сохраняет accepted registry, подпись старого ключа и проверки CA fingerprint/EKU/SAN.

Изолированные accepted images и настоящие локальные P-256/CSR/X.509; зависимости fixture EnrollmentCertificateFixture не открывают сеть/БД/файлы и не запускают host. Snapshot validation проверять также в recovery ancestors. Сначала подтверждать валидный graph/DER, затем вносить одну мутацию.

Audit проверять по фактическому результату reducer после accepted перехода: system provenance, отсутствие success при отказе, dedup известного OperationId и отсутствие DER/SPKI/proof в логах.

Authority/signing tests используют настоящий локальный crypto/защищённый issuer secret во временном каталоге, in-memory signing UoW и typed подставной transport. Barrier/process leases — явные doubles, не native consensus. Конкуренцию организовывать TaskCompletionSource внутри issuer lock. Проверять exact authorization до подписи и повтор после ответа, single durable DER при loss/replay/новом leader, запрет Bind/Ready и закрытый dispatcher отдельными Application regression tests. Эти проверки не подтверждают реальный HTTP/TLS, legacy CRL, durable local voter key install, SQL/fsync или весь lifecycle последующего voter; положительный second-rotation test использует синтетический accepted Bind/retirement/process lease.

Проверки ConfirmBindingAsync сначала получают signed artifact и подтверждают exact Bind на исходном срезе. Затем меняют одно поле команды, доказательство, локальную identity, состояние прежнего leaf, TLS boundary или поколение recovery. Это проверка pre-dispatch authority.

Continuation proof проверяется прямым вызовом с in-memory accepted registry и локальным managed leaf. Использовать custom-root validation без загрузки сертификатов и online CRL. Отдельно проверять отказ live policy и обычного renewal на точной границе истечения, разделение подписей и конфликт process lease.

Accepted ContinueCredential проверяется с in-memory консенсусом, trusted common actor и login double. Использовать фактические codec, reducer и snapshot format, сверять первый результат при WAL replay и потерянном ответе. Только тестовый каталог snapshot допускает файловый I/O. SQL, сокеты, host и native consensus в тестовый граф не подключать.

Для HTTPS authority проверять полный purpose-bound обмен challenge/nonce/PoP, exact read по принятому запросу, изоляцию status/registration nonce и отказ после смены authority или истечения. Чтение и повтор с уже потреблённым nonce не добавляют WAL-записей.

Выдачу ContinueCredential проверять с production signer и общим DER journal поверх memory UoW и локального тестового issuer. Моделировать потерю ответа после сохранения, конкурирующие boot, повтор после восстановления accepted WAL и смену полномочий между сохранением и ответом. Производственная SQL durability и реальные выборы остаются вне этого набора.

Bind ContinueCredential проверять тем же графом с production reducer, snapshot и аудитом. Воспроизводить потерянный ответ после apply, отзыв прав на apply и потерю majority после чтения журнала. Проверять повтор по свежему nonce, неизменную CredentialRevision и одну запись DER.

Допуск нового boot после Bind проверять с production PoP и authority поверх того же изолированного графа. Отдельно проверять expiry, принятие конкурирующего процесса, смену term/recovery, отзыв и exact replay решения без повторной выдачи.
