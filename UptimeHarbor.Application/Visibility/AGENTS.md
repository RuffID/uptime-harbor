# Политика видимости

`ObjectVisibilityPolicy` применяется панелью и определением SignalR-получателей. `MonitoringEventRecipientResolver` работает с отдельным materialized `IMonitoringRecipientReadQuery`.

Администратор видит все объекты; обычному пользователю для контейнера нужны права и на него, и на сервер. Список пользователей, роли и permissions читать пакетно, без per-user запросов.
