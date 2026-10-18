# Снимок главной панели

`DashboardQuery` получает materialized snapshot через `IDashboardSnapshotReadQuery`, применяет `ObjectVisibilityPolicy`, затем поиск и формирование DTO. Время snapshot и uptime определяются через `TimeProvider`.

Не раскрывать endpoint URL. Поиск выполняется только по разрешённым объектам; заданное имя сервера и внутреннее имя остаются разными полями. Это read-only сценарий без искусственного Unit of Work.
