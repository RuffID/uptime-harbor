# Модели главной панели

Read records и `DashboardReadSnapshot` — независимый результат инфраструктурной проекции; dashboard DTO — результат для Web.

Не включать endpoint URL и EF entities. Оба типа передают административное Name и InternalName; ServerId приложения необязателен. Сохранять UTC timestamps и совместимые Containers / UnassignedContainers; UI объединяет их в список.

Read record сервера содержит nullable `StartedAtUtc`; серверный DTO передаёт его и nullable `UptimeSeconds` наравне с контейнером.
