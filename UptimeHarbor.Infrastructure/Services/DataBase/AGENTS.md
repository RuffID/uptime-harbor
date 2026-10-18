# Startup-проверка и backup SQL Server

`DataBaseCheckUpService<TContext>` проверяет подключение, получает pending migrations, вызывает backup и затем migration. `BackupService<TContext>` создаёт SQL Server backup, а path builder формирует имя файла.

Ошибка подключения, backup или migration останавливает startup до Identity bootstrap. Не логировать connection string, credentials и SQL. Пути на Linux должны соответствовать доступному SQL Server backup-каталогу; проверка исходников не разрешает выполнение SQL.
