# HTTP API

Контроллеры разделяют защищённое чтение панели и административные операции с объектами, пользователями и уведомлениями. Они вызывают Application и используют `JsonResultMapper`.

Сохранять authentication, administrator role и antiforgery на изменениях. Для автоматических model-state ошибок применяется общий error envelope; не добавлять EF-запросы и бизнес-правила в controller.

Endpoint назначения ресурсных permissions удалён. Общий sign-in и переключение пользовательских writers на command pipeline выполняются отдельно; cluster mode сохраняет browser gate закрытым.
