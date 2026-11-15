# Изолированные peer enrollment tests

Вызывать production client/service/controller и mTLS middleware с fake HttpMessageHandler, локальными P-256 сертификатами и in-memory native ConsensusOnlyState/ManagedRaftProbe без Start. Не использовать sockets/listeners, настоящий DNS, SQL/SQLite или второй OS-процесс.

Synthetic accepted voters и fake barriers проверяют typed routing, origin/process/template/epoch и повтор исходного intent/DER. Они не доказывают live elections/majority, TLS handshake, SQL durability или distributed file atomicity. Для native store не использовать DispatchProxy<IPersistentState> (DotNext internal setter); создавать in-memory ConsensusOnlyState.

Проверять leader/follower роли отдельно от signer owner; lost response не меняет IssueId/DER и не выполняет Bind автоматически. Общий commands batch, nonvoter, browser/pending session и arbitrary Sign(bytes) остаются закрытыми. Подпись grant без accepted record не открывает gates.
