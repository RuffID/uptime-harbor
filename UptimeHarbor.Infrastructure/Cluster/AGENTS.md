# Cluster infrastructure

- DotNext 6.10.0 — единственный алгоритм Raft. WAL с CRC64, flush на append/commit и persistent configuration отделены от SQL.
- Восстановить checkpoint/snapshot до создания WAL; неизвестный checkpoint запрещает создание и старт. Не обнулять manifest/state/configuration при отказе.
- Ошибку apply держать latched до подтверждённого Restore/replay: успешный CanConnect не восстанавливает исправность проекции. Сообщения исключений reducer/provider не выпускать в DotNext/HTTP — только безопасную ошибку без inner payload.
- Состав изменять по одному участнику через DotNext после принятой авторизации Administrator; health не удаляет участников. Не регистрировать failure detector removal.
- Peer transport использует отдельные mTLS identity/limits и отключённые cookies/redirect/HTTP logging. Не доверять proxy certificate header на пользовательском listener.
- В логах только безопасные ID/reason codes; raw exceptions, URI и payload не журналировать.
