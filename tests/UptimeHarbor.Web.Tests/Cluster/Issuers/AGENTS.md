# Проверки manual issuer

Production provisioning, encrypted files, Pending, dispatcher/reducer/apply, registration/read/signing barriers проверяются без SQL, native Start и сокетов. Native API и peer transport — управляемые doubles; PeerClientHandler сохраняет проверку authenticated envelope. Проверять capability каждого voter, прежние key/DER/command на retry, отсутствие выдачи до apply и сохранность legacy credentials. Эти проверки не подтверждают реальные TLS/majority/SQL или Ubuntu volumes.

Для in-memory native audit trail использовать ConsensusOnlyState с заданным term. DispatchProxy для IPersistentState в DotNext 6.10.0 не создаётся из-за недоступного setter ConfigurationStorage; proxy допустим для IRaftCluster, без Start и выборов. Синтетическая accepted issuance в signer-тесте проверяет policy/crypto, а не повторяет весь enrollment E1–E4.
