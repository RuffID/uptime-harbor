# Модели главной панели

Read records и `DashboardReadSnapshot` — независимый результат инфраструктурной проекции; dashboard DTO — результат для Web.

Не включать endpoint URL и EF entities. Сохранять UTC timestamps, раздельные имена сервера и необязательный `ServerId` контейнера; snapshot передаёт `UnassignedContainers` отдельно; JSON-названия проверяются Web-контрактами.

Read record сервера содержит nullable `StartedAtUtc`; серверный DTO передаёт его и nullable `UptimeSeconds` наравне с контейнером.
