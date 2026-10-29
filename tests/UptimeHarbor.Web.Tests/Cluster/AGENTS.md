# Изолированные проверки cluster hosting

- Вызывать middleware и DI напрямую, без host/start, слушающих портов и реальных запросов.
- Persistent configuration DotNext проверять в отдельном временном каталоге. Не запускать Raft-узлы и не называть store/API tests проверкой выборов или crash recovery.
