# Снимок главной панели

`DashboardQuery` проверяет единую `IAdministratorAccessPolicy` до чтения materialized snapshot через `IDashboardSnapshotReadQuery`, затем выполняет поиск и формирование DTO по всем объектам. Ресурсная ACL-фильтрация не используется. Время snapshot и uptime определяются через `TimeProvider`.

Контейнеры без сервера возвращаются администратору отдельной коллекцией `UnassignedContainers`, с теми же поиском и UTC/uptime правилами.

Не раскрывать endpoint URL. Поиск выполняется только по разрешённым объектам; заданное имя сервера и внутреннее имя остаются разными полями. Это read-only сценарий без искусственного Unit of Work.
