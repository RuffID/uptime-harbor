# Материализованные read queries

`DashboardSnapshotReadQuery` проецирует данные панели без endpoint URL и возвращает все цели администратору. `MonitoringRecipientReadQuery` получает пользователей и роль администратора одним запросом; коллекции прежних permissions пусты.

Ресурсных permissions в целевой модели нет. Не добавлять ACL-запросы для совместимых Application records. `HistoryRetentionQuery` только определяет защиту истории: незавершённая доставка, unknown attempts, recovery entitlement, ссылки сводки и открытая observation assembly; worker очистки здесь отсутствует.

Результаты — независимые Application records, полностью материализованные здесь. Не выпускать `IQueryable` и EF entities за границу; сохранять no-tracking и отсутствие N+1.
