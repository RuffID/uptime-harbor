# Telegram-уведомления

После объединения целей Server / Application оба типа в списке Telegram-привязок показываются по административному Name; полученное внутреннее имя остаётся metadata проверок. Совместимые поля bindings ServerId / ContainerId ссылаются на одну monitored_servers. Удаление сервера снимает родителя у приложений, сохраняя их мониторинг и собственные привязки.

## Управление и результаты в UI (08)

Каждый активный принятый Administrator управляет общими Chat/Binding и независимыми NodeChatRoute по NodeId через typed Pending-команды. UI поясняет текущую проблему при новой привязке, отмену не начатого outage при unbind и заслуженный final recovery после успешного/позднего in-flight outage. Глобальное удаление показывает зависимости и требует подтверждения.

Test-send с любого узла сохраняет стабильный OperationId и подаётся в принятую очередь главного; HTTP 202 означает local Pending, Committed — принятие команды очереди. Доставка видна отдельно по accepted intent/настоящим attempts. Terminal Failed и безопасная категория причины остаются в журнале; ручного retry нет. Telegram readiness Available/Unavailable/Unknown с известной причиной/возрастом отделена от ошибки отдельного чата и coverage. Новых sender-вызовов, proof или открытых gates UI не создаёт.


## Принятая доставка этапа 07

Enabled-путь восстанавливает очередь непосредственно из принятого MonitoringImage: incidents, events, recipients/intents, immutable summaries и полная история attempts/results. Локальный legacy outbox и его claim не участвуют. DeliveryQueue.Validate проверяет историю до renewal; известный accepted success, Cancelled и terminal Failed не становятся due. Logical key incident + kind + chat сохраняется при rebind; Update дополнительно различает event. BindingGeneration проверяется отдельно. Инцидент Node использует принятые NodeChatRoute именно своего NodeId.

DeliverySenderConsensus принимает RenewSender/BeginDelivery через существующие RevisionDispatcher → DotNext → RevisionApplyEngine. AcceptedDeliveryDispatcher повторно проверяет authority/term/majority, RecoveryVersion, readiness, intent и текущую конфигурацию непосредственно перед внешним вызовом. Initial, retry, recovery, summary и test используют один путь. После смены роли действует drain 12 секунд, authority обновляется примерно каждые 5 секунд независимо от in-flight transport и истекает через 10 секунд. Scope/claim не заменяет это право. Отмена после начала HTTP может не остановить внешний эффект.

Telegram transport требует HTTP success, JSON ok=true и настоящий положительный message_id. После вызова результат сохраняется через локальный IDeliveryResultUnitOfWork и оперативно передаётся существующим revision transport; отменённый leadership token не отменяет эту фиксацию. Локальный capture ещё не является общим успехом. RecordDelivery проверяет admitted NodeId/storage generation и исходного отправителя попытки; поздний success монотонно сохраняется даже после принятого ambiguous failure. Попытка с неизвестным результатом после 30 секунд допускает AbandonDelivery/Ambiguous и ограниченный retry. Потеря результата между Telegram и capture/commit допускает редкий дубль; exactly-once не заявляется.

На intent — максимум пять реальных attempts, backoff 5/30/120/300 секунд; retry_after увеличивает задержку. Закрытый формат FailureKind RateLimited/<seconds> хранит 1–86400 секунд в существующем поле, в лог идёт только категория. Network/Timeout/Unavailable/InvalidResponse/Ambiguous допускают retry; InvalidConfiguration/InvalidToken/InvalidChat/ChatNotFound/Forbidden завершают intent. Ошибка чата не вызывает resign; до четырёх доставок запускаются независимо. Terminal exhaustion остаётся в accepted history, ручной restart отсутствует. Ответ Bot API ограничен 64 KiB; URI, token, body и текст сообщения не логируются.

Unbind отменяет не начатые problem/update; начатый outage может завершиться success и дать один заключительный recovery, в том числе когда восстановление уже принято. Recovery получают только чаты с подтверждённой проблемой того же incident; после удаления цели/чата, disable или смены check generation новые вызовы запрещены. Новая привязка получает открытую проблему, rebind не повторяет известный успех. Неначатая устаревшая summary part отзывается целиком, её состав и исходные events/intents сохраняются; успешная summary имеет собственный настоящий attempt/result.

Текст различает partial/full/recovered и отдельные coverage warnings. Восстановление перечисляет прежние проблемные направления; metadata имени/времени запуска остаются по observer NodeId, общее имя/uptime не синтезируется. Test-send с follower сохраняется через ClusterTestDelivery/PendingCommandService, затем принимается главным; активный accepted Administrator проверяется при подаче, принятии и перед transport. HTTP 202 означает постановку, фоновая доставка не требует пользовательской сессии. Токен остаётся локальным, видимость чата не меняет маршрутизацию.

Default Disabled сохранён; наличие компонентов не включает отправку. UI и controlled enable реализованы, зависимости открываются только после подтверждённых условий и ручного разрешения. Emergency baseline сохраняет настоящие attempts/results и original authority с точным provenance; известный success и terminal exhaustion не запускаются заново. Токен одного общего бота локален у каждого возможного отправителя, не реплицируется и не выводится в диагностике. При аварии специалист отзывает прежний токен того же бота до новой активации. Telegram-статус не обещает срок восстановления/countdown. [Runbook](Cluster%20recovery.md), [проверки](Testing.md#проверка-этапа-09).

## Принятые инциденты и состав доставки (06)

Unbind/удаление источника отзывает не начатую часть сводки целиком, сохраняя её неизменяемый исторический состав. Остальные допустимые источники возвращаются в Planned для нового плана; отозванные отменяются. Удаление цели блокирует также её старые завершённые intents и recovery, а удаление чата — все его не начатые sends.

`MonitoringIncidents` принимает одну историю Target/Node и отдельную Coverage history. Stable incident/event IDs и logical intent key определяются принятой историей; неизменное состояние/replay не создают повтор. Все incident/event/intents фиксируются тем же projection apply, без локального autocommit.

Обычные цели используют bindings, узлы — отдельные NodeChatRoute по NodeId. Recovery планируется только для чатов с настоящим accepted successful DeliveryAttempt. Unbind отменяет не начатые problem/update, сохраняя право уже уведомлённого чата на recovery; удаление чата отменяет и recovery. При неизвестном delivery proof восстановление получателей/состав backlog не подменяется успехом.

`MonitoringBacklog` сохраняет durable part/cut/source IDs и текст до 3500 символов. Давние завершённые аварии входят в сводки без age cutoff, открытые остаются отдельными intents; исходные events/intents не удаляются. Источники помечаются SuppressedBySummary, без фиктивного delivery confirmation. Реальные попытки, sender lifecycle, renewal и dispatcher подключены этапом 07 за общим gate. Подробности — [Monitoring.md](Monitoring.md#распределённый-путь-этапа-06).

## Граница этапов 03/04 и 05.1

Ниже описана локальная доставка при `Cluster:Mode=Disabled`. Enabled-режим не регистрирует прежний outbox worker; `ClusterNotificationSender` отвергает legacy-вызовы без принятого AttemptId. Accepted delivery подключена отдельно, за закрытыми readiness dependencies. `SenderAuthorityGate` проверяет принятую authority/attempt, большинство, generation/epoch и readiness; реальные authority/attempt решения и delivery worker подключены в 07; 04 реализует конфигурационные команды/привязки, без Telegram delivery. `TelegramHealthProbe` вызывает только `getMe` через отдельный клиент без HTTP logging, сверяет BotId, ограничивает timeout/body и сохраняет известную причину/возраст. Ошибка одного чата и rate limit не объявляются общим outage; тестовые сообщения health не отправляет. Подробности и ограничения — [Cluster runtime.md](Cluster%20runtime.md).

05.1 повторяет administrator policy в `NotificationAdministrationService` после чтения чата непосредственно перед внешним test-send. В cluster mode policy читает active accepted account; disabled/deleted/unaccepted актор не вызывает sender. Cluster test-send подключён через Pending и главного; новый UI статуса остаётся этапом 08. 04 сохраняет конфигурационные изменения привязок, не сбрасывая подтверждённые markers; tombstones блокируют удалённые зависимости. Этап 06 принимает incident events/intents без подтверждения отправки; production delivery decisions реализованы в 07.

## Состояние реализации

Application содержит независимые `INotificationSender`, административный use case и use case подготовки/фиксации доставки. Infrastructure содержит HTTP adapter Bot API, durable outbox dispatcher и отдельный hosted worker. Web содержит административный MVC API `/api/admin/notifications` и AJAX-only страницу `/Admin/Notifications` на TypeScript. Текущая persistence-схема входит в SQL Server baseline `20260831015604_InitialSqlServerSchema`.

## Хранение

`TelegramChat` содержит внутренний `Guid`, уникальный Telegram chat ID и имя для администратора.

`NotificationBinding` связывает чат ровно с одним сервером или контейнером. Это правило защищено доменной фабрикой и check constraint. Уникальные filtered indexes запрещают повторную привязку одной цели к одному чату.

Объект мониторинга хранит `CurrentIncidentId`. Каждая привязка хранит `OutageDeliveredIncidentId` и `RecoveryDeliveredIncidentId`. Эти поля описывают только текущее состояние доставки одного последнего актуального инцидента и перезаписываются новым инцидентом.

`notification_outbox_jobs` хранит структурированный JSON payload с monitoring event и необязательными причиной/временем отмены, число попыток, время следующей попытки, lease и terminal timestamps. Job не имеет внешнего ключа к объекту: уже сохранённое уведомление не исчезает при последующем административном удалении. Отмена не удаляет job: она записывает диагностическую причину в payload и использует существующий `CompletedAtUtc` как терминальный признак, поэтому изменение не требует новой колонки или правки SQL Server baseline.

## Поток доставки

1. При `BecameUnavailable` или `Recovered` monitoring payload сохраняется в outbox одной транзакцией с новым состоянием объекта.
2. Monitoring check завершается без ожидания Telegram HTTP transport.
3. Отдельный worker claim-ит due jobs lease на две минуты и обрабатывает до четырёх jobs параллельно.
4. Получатели одного job запускаются параллельно с лимитом четыре; зависание или ошибка одного чата не задерживает начало остальных.
5. После `Recovered` recovery отправляется только привязкам с совпадающим `OutageDeliveredIncidentId`.
6. Успешные привязки получают marker в отдельных scopes, а job завершается только когда нет неуспешных получателей.
7. Отключение мониторинга или смена endpoint в транзакции административного update помечают все незавершённые recovery jobs прежнего объекта/incident как отменённые.
8. Перед вызовом sender dispatcher выполняет условный транзакционный gate по строке job. Если отмена зафиксирована первой, callback доставки не запускается. Если gate получен первым, строка удерживается до завершения попытки, поэтому административный commit произойдёт только после уже начавшейся отправки.

Сообщение включает тип объекта, внутреннее имя из endpoint или идентификатор, описание при наличии, новый статус и UTC-время события. Для контейнера с сервером также указывается заданное администратором имя родительского сервера; у контейнера без сервера эта строка отсутствует, привязки и доставки продолжают работать; recovery может включать время запуска и uptime. В списке целей настройки уведомлений серверы также показываются по административным именам.

## Частичная доставка

Если объект связан с несколькими чатами, состояние доставки нужно учитывать по каждой привязке текущего инцидента. Успех одного чата не должен скрывать ошибку другого, а retry не должен повторно отправлять сообщение уже успешному получателю.

Успешная отправка фиксируется отдельным коротким scope и `INotificationSettingsUnitOfWork` для конкретной привязки. Повторная обработка выбирает только привязки без соответствующего marker. Ошибка одного transport-вызова не изменяет уже сохранённый monitoring status.

Retry ограничен пятью попытками. Между неуспешными попытками используются интервалы 5 секунд, 30 секунд, 2 минуты и 5 минут; после пятой job остаётся в БД с `FailedAtUtc` для диагностики и не запускается автоматически.

Отменённая job идемпотентно исключается из claim и повторной обработки. Обычная recovery job без административного reset продолжает использовать прежние retry и at-least-once правила. Транзакционный gate относится только к Telegram worker и административному update; monitoring cycle не ждёт transport.

## Конкурентные административные конфликты

Предварительная проверка chat ID и пары binding остаётся удобной валидацией, но не считается защитой от гонки. SQL Server unique errors 2601/2627 преобразуются Infrastructure-слоем в независимый `PersistenceConflictException` только для EF entries `TelegramChat` и `NotificationBinding`, чьи unique indexes имеют стабильные известные имена. Application возвращает безопасный `ServiceResult.Conflict`, Web — HTTP 409 и `{ success: false, message, field? }`. Текст SQL Server, connection string, имя таблицы/index и provider exception в ответ не передаются. Прочие `DbUpdateException` не маскируются как ожидаемый конфликт.

## Семантика доставки

Transport-гарантия — **at-least-once**, не exactly-once. Outbox исключает потерю локального задания при перезапуске, но Telegram Bot API `sendMessage` не принимает idempotency key. Если Telegram уже принял сообщение, а процесс завершился до фиксации per-binding marker, следующая попытка может отправить дубликат. Обратный порядок создавал бы риск потери, поэтому exactly-once в текущей архитектуре честно не заявляется.

## Секреты и логирование

Bot token поступает через единственный `TelegramOptions`, не хранится в БД и не выводится в логи. Стандартное HTTP-логирование Telegram client отключено, потому что token входит в Bot API URL. Ошибки доставки логируются только с внутренними GUID объекта, привязки и чата, без URI, response body и текста сообщения.

## Проверка настроек и тестовая отправка

Формат токена проверяется при старте и перед HTTP-вызовом; заглушка из конфигурационного примера не допускается. Bot API URI задаётся как относительный `./bot{token}/sendMessage`, чтобы двоеточие внутри token не интерпретировалось как URI scheme. Успех требует успешного HTTP-статуса и JSON `ok: true`.

Transport передаёт безопасную классификацию через `NotificationDeliveryFailure`/`NotificationDeliveryException`. Тестовая отправка возвращает понятную причину и действие: проверить токен в `Config/config.json`, ID чата, нажать «Старт» у бота или добавить его в группу, проверить разрешения, повторить позже при лимите/сбое либо проверить соединение при timeout/network error. Произвольный текст ответа Telegram и transport exception не раскрываются. Синтаксически правильный токен может оказаться отозванным; локальная валидация этого не определяет.

Чаты без привязок остаются в таблице `/Admin/Notifications`; ячейки типа объекта, объекта и действий привязки пустые, без прочерков.

## Аккаунтная авторизация после этапа 05

Enabled account sign-in и локальные сессии используют принятое состояние; test-send сохраняет повторную AcceptedAdministratorAccessPolicy перед sender. Pending пользователь/пароль не предоставляет доступ. Подключение Identity не меняет закрытые sender/worker/activation/delivery зависимости; компоненты распределённой доставки реализованы в 07, реальные Telegram-проверки не выполнялись.
