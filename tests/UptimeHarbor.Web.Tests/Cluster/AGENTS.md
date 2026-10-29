# Изолированные проверки cluster hosting

- Monitoring revision boundary проверяет совпадение storage generation с authenticated peer до dispatch; composition tests должны разрешать новые worker/pump/coordinator без host/WAL/HTTP, сохраняя закрытые gates.

- Вызывать middleware и DI напрямую, без host/start, слушающих портов и реальных запросов.
- Persistent configuration DotNext проверять в отдельном временном каталоге. Не запускать Raft-узлы и не называть store/API tests проверкой выборов или crash recovery.
- AccountSessionTests вызывает production apply/reducer/session и fake HubCallerContext: password/stamp/disable/delete/fault должны отзывать защищённую подписку, обычный offline/recheck — сохранять её. Это не WebSocket/browser acceptance.
- Controller batch проверять до consensus dispatch, включая malformed tail и отсутствие verified peer item. Cluster composition должна разрешать production recovery с закрытым admission, не создавать native state в DI validation.
