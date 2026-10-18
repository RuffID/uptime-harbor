# Материализованные read queries

`DashboardSnapshotReadQuery` проецирует данные панели без endpoint URL. `MonitoringRecipientReadQuery` получает пользователей и роль администратора одним запросом и permissions пакетно.

При чтении container permissions проверять совпадение сохранённого server scope с текущим `MonitoredContainer.ServerId`, чтобы устаревшие права после переноса не открывали объект.

Результаты — независимые Application records, полностью материализованные здесь. Не выпускать `IQueryable` и EF entities за границу; сохранять no-tracking и отсутствие N+1.
