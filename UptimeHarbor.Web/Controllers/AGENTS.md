# HTTP API

Контроллеры разделяют защищённое чтение панели и административные операции с объектами, пользователями и уведомлениями. Они вызывают Application и используют `JsonResultMapper`.

Сохранять authentication, administrator role и antiforgery на изменениях. Для автоматических model-state ошибок применяется общий error envelope; не добавлять EF-запросы и бизнес-правила в controller.

Endpoint назначения ресурсных permissions удалён. В enabled-режиме Users GET читает принятые аккаунты, POST commands требует явные OperationId/UserId/base revision и возвращает 202 для durable Pending. GET operations показывает только безопасный принятый итог; legacy writers запрещены accepted store. Cluster mode сохраняет browser gate закрытым до будущего controlled enable; наличие account API не разрешает раннее открытие.
