# HTTP API

ClusterAdministrationController предоставляет accepted snapshot, постраничный local Pending, безопасный operation result и общую диагностику через /api/admin/cluster. Изменения требуют typed settings/OperationId/base revision, Administrator и antiforgery; LocalPending возвращает 202. Legacy read models и writers в enabled отклоняются фильтром; общие Users read/commands и accepted test-send сохраняются. Disabled использует прежние маршруты.

Контроллеры разделяют защищённое чтение панели и административные операции с объектами, пользователями и уведомлениями. Они вызывают Application и используют `JsonResultMapper`.

Cluster test-send возвращает 202 после durable постановки через IAcceptedTestDelivery; это не подтверждение Telegram success. Ошибки сохраняют общий envelope и исходный статус; Disabled test-send остаётся синхронным.

Сохранять authentication, administrator role и antiforgery на изменениях. Для автоматических model-state ошибок применяется общий error envelope; не добавлять EF-запросы и бизнес-правила в controller.

Endpoint назначения ресурсных permissions удалён. В enabled-режиме Users GET читает принятые аккаунты, POST commands требует явные OperationId/UserId/base revision и возвращает 202 для durable Pending. GET operations показывает только безопасный принятый итог; legacy writers запрещены accepted store. Cluster mode сохраняет browser gate закрытым до будущего controlled enable; наличие account API не разрешает раннее открытие.
