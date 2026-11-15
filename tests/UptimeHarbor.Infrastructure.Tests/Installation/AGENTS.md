# Изолированный setup

ProgressValidationTests вызывает production LocalSetupAccountStore через MemoryContext/DbSet над памятью, проверяет полный обход и размер страниц, историю больше 10000, corruption и отмену. SQL Server query shape проверяется ToQueryString на закрытом context, без выполнения SQL. Эти tests не подтверждают provider ordering/locking или конкурентные записи в реальную БД.

EnrollmentDynamicTlsTests повторяет live registry/credential/issuer revocation после TLS installation и проверяет name/chain/pin/expiry/orphan без sockets. Installation namespace допускает точный filter-class после проверки fixtures: providers не открываются, model tests используют только закрытый context metadata. Полный Infrastructure project не запускать по этому основанию.

Enrollment* tests используют реальные локальные P-256/CSR/X.509 и production codecs/reducer/journal/signer, fake majority authorization, HttpMessageHandler и локальные временные файлы. EnrollmentJoinScenario проверяет Committed у каждой предпосылки. GenesisWriterPermit tests доказывают exact payload gate, не native startup/elections. DNS проверяется на переданных IP без resolver/network; TLS pin policy — на локальных сертификатах без handshakes/listeners. Metadata/migration model diff не выполняют SQL. Эти тесты не подтверждают production admission или file/SQL atomicity.

EnrollmentDescriptorTests проверяет immutable IssueId/template/DER даже при корректном новом leaf от прежнего CA, origin/capabilities/ClusterId/CSR/digest/pin и границу admitted nonvoter без activation. Повтор точного descriptor и read-state/resume/process-renew до Bind должны сохранять прежние DER.

Тесты используют in-memory сценарные repositories/UoW, metadata закрытого SQL Server context и временные локальные файлы. SQL/SQLite, host, второй OS-процесс, сеть и Telegram не запускаются. Второй file handle проверяет межпроцессный примитив, но не заменяет Ubuntu/ACL/fsync приёмку. Doubles authority никогда не регистрируются production модулем.
