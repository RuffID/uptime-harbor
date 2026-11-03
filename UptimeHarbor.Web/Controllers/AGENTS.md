# HTTP API

SetupController отделён own cookie scheme от common API. Login/prepare/enroll/logout требуют antiforgery, exact Origin/Host и rate limit. Enroll принимает bounded origin/invitation без actor/key/stamp, возвращает только safe SetupStatus; секрет и protected evidence не сериализовать в UI. Bootstrap подключает production genesis lifecycle; enroll до E4 не открывает общий доступ. EnrollmentController — отдельный strict bounded PoP protocol без cookie auth; действия purpose-bound, raw exceptions/payload не журналировать.

ClusterInvitationsController обслуживает только Administrator API выдачи/явной замены/отзыва/Continue/Cancel и safe read. Mutations требуют antiforgery, rate limit и password reauthentication; actor берётся сервером. 202 означает существующий local Pending, Unconfirmed не выдумывает очередь. Новых экранов и анонимного enrollment endpoint пока нет.

AdminObjectsController сохраняет совместимые routes servers / containers для Server / Application. Аудит создания/изменения/удаления записывает фактический итог, тип, ID и доверенный actor ID; имена, URL, описания и raw errors не журналировать.

ClusterOperationsController обслуживает lifecycle GET/POST с accepted Administrator/antiforgery. 202 означает Pending, internal lifecycle через endpoint запрещён. Safe registry не выдаёт public certificate/payload/hash/stamps; обычные resource editors не принимают Node-команды.

ClusterAdministrationController предоставляет accepted snapshot, постраничный local Pending, безопасный operation result и общую диагностику через /api/admin/cluster. Изменения требуют typed settings/OperationId/base revision, Administrator и antiforgery; LocalPending возвращает 202. Legacy read models и writers в enabled отклоняются фильтром; общие Users read/commands и accepted test-send сохраняются. Disabled использует прежние маршруты.

Контроллеры разделяют защищённое чтение панели и административные операции с объектами, пользователями и уведомлениями. Они вызывают Application и используют `JsonResultMapper`.

Cluster test-send возвращает 202 после durable постановки через IAcceptedTestDelivery; это не подтверждение Telegram success. Ошибки сохраняют общий envelope и исходный статус; Disabled test-send остаётся синхронным.

Сохранять authentication, administrator role и antiforgery на изменениях. Для автоматических model-state ошибок применяется общий error envelope; не добавлять EF-запросы и бизнес-правила в controller.

Endpoint назначения ресурсных permissions удалён. В enabled-режиме Users GET читает принятые аккаунты, POST commands требует явные OperationId/UserId/base revision и возвращает 202 для durable Pending. GET operations показывает только безопасный принятый итог; legacy writers запрещены accepted store. Cluster mode сохраняет browser gate закрытым до будущего controlled enable; наличие account API не разрешает раннее открытие.
