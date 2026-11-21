# HTTP request-модели

RegisterEnrollmentIssuerRequest принимает только OperationId и ограниченный пароль, запрещает unknown JSON fields и редактирует ToString. Epoch/issuer/node/actor определяет сервер; public material и key reference из браузера не принимать.

ConfirmVoterContinuationRequest требует digest выбранного принятого запроса и ограниченный пароль. Неизвестные поля отклоняются. Actor берётся из доверенного ticket, ID запроса из маршрута. ToString не раскрывает тело.

Setup requests запрещают unknown JSON fields; не принимают BotId, NodeId, stamp, authority evidence или private material из браузера. Парольные DTO имеют редактированный ToString и не журналируются целиком.

IssueInvitationRequest/InvitationActionRequest не принимают actor, verifier или внутренние команды. Неизвестные JSON-поля отвергаются, пароль ограничен и redacted в ToString; plaintext Code появляется только в специальном первом ответе, не в URL/SignalR.

CancelPendingRequest содержит только новый OperationId отмены. Target загружается сервером из защищённой local Pending; actor, payload, digest и provenance из запроса не принимать.

Отдельные request-типы описывают настройки объектов, пользователей и Telegram bindings; resource permissions отсутствуют.

Настройки приложения передают Name и nullable ServerId (null снимает связь); сервер сохраняет optional ContainerLinks для совместимости. Вложенный POST и общий POST containers используют одну команду. Тип задаётся маршрутом и неизменяем после создания.

Сохранять контракты JSON и validation, `Version` для stale updates и подтверждённое количество контейнеров при удалении сервера. Token Telegram и persistence entities в запросы UI не добавлять.
