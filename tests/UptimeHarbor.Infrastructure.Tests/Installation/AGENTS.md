# Изолированный setup

Тесты используют in-memory сценарные repositories/UoW, metadata закрытого SQL Server context и временные локальные файлы. SQL/SQLite, host, второй OS-процесс, сеть и Telegram не запускаются. Второй file handle проверяет межпроцессный примитив, но не заменяет Ubuntu/ACL/fsync приёмку. Doubles authority никогда не регистрируются production модулем.
