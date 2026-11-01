# Политика видимости

`ObjectVisibilityPolicy` применяется панелью и определением SignalR-получателей. `MonitoringEventRecipientResolver` работает с отдельным materialized `IMonitoringRecipientReadQuery`.

Администратор видит все объекты, включая контейнеры без сервера; другие пользователи не получают доступ через прежние permission-наборы. Ресурсных назначений нет. Users/роль читать пакетно, compatibility collections возвращать пустыми; без per-user запросов. Общий accepted sign-in/SignalR подключается отдельно в 05.2.
