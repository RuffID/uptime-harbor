# Движок мониторинга

## Браузерное представление (08)

AdministrationMonitoring строит read-only представление свежести принятых observations для целей и зарегистрированных узлов. Availability Available/Partial/Unavailable/Unknown отделена от coverage Complete/Initializing/Degraded/Lost. У каждого назначенного observer показаны статус, время последней проверки, безопасная категория ошибки, его metadata и расчёт uptime с указанием исходного startedAtUtc/metadata timestamp. Missing/Stale не объявляются успешным coverage. Отличающиеся name/startedAtUtc отмечены, общего имени/uptime из них нет. Чтение не принимает новый факт, evaluation или incident; запись observers проходит существующий Pending pipeline.


## Доставка принятой истории (07)

История этапа 06 является источником очереди DeliveryQueue. Реальные BeginDelivery/RecordDelivery/AbandonDelivery обновляют attempts, intents и accepted evidence тем же reducer/apply; прежний guard DeliveryAttemptProjectionRequired не обходится синтетическими попытками. Late outage success после unbind сохраняет право на один recovery, а superseded/deleted target блокирует новые вызовы. Состав сводки и исходные события сохраняются после success/replay.

Dispatcher не запускает endpoint checks и не меняет расчёт availability/coverage. Сообщения различают partial/full/recovered и восстановление наблюдения, сохраняя metadata по observer. Общий gate остаётся закрытым при неизвестной dependency; подробности — [Notifications.md](Notifications.md#принятая-доставка-этапа-07).

## Распределённый путь этапа 06

Старое наблюдение допускается в historical-only evidence только по реально принятой версии конфигурации, где источник был назначенным observer. Неизвестное поколение или выдуманное прежнее назначение отвергаются. Unbind/delete отзывает неотправленную summary part без изменения её принятого состава, остальные источники возвращаются для нового плана.

В enabled-режиме `ClusterMonitoringWorker` вызывает bounded cycle, durable observation pump и coordinator. Все рабочие проверки требуют настоящих `NodeReadiness.CanWork`, recovery и доступного accepted image. `Disabled` завершает новый worker без действий и сохраняет прежний однопроцессный путь ниже. Lifecycle 09 открывает gates только после реальных предпосылок и отдельного подписанного ручного enable; фактического включения не было. Maintenance закрывает UI/checks/sender и остаётся latched после restart. [Процедура](Cluster%20operations.md#обслуживание-обновление-и-rollback).

`ConfigurationValue.ObserverNodes`, CheckGeneration, ConfigRevision, failure threshold и interval/timeout принадлежат принятой конфигурации цели. Endpoint/enable/набор observers/порог/периоды требуют нового CheckGeneration; изменение metadata или родительской связи сохраняет поколение. Проверяются только назначенные observers; пропавший observer автоматически не исключается. До восьми независимых server/container checks выполняются параллельно, каждый capture имеет отдельный DI scope; отказ сервера не останавливает контейнер. HTTP/JSON-валидация остаётся общей с Disabled.

`ObservationCaptureService` через существующий `IObservationCaptureUnitOfWork` атомарно резервирует sequence и сохраняет payload/digest. Поток определяется target + observer NodeId + storage generation + check generation. Singleton capture gate сериализует scopes одного процесса; это не SQL locking между процессами. Retry ID возвращает исходный capture, изменённый payload отвергается. После endpoint заново проверяется принятое назначение/поколение и RecoveryVersion. Без главного результат остаётся локально; Telegram не вызывается.

`ObservationReplicationPump` читает страницы до 100 streams/100 results и передаёт команды существующим revision transport. ACK — только принятый frontier; captures не удаляются. Reducer проверяет storage provenance, strict sequence и digest ID. Gap отвергает пакет без частичного эффекта. Старые/disabled/deleted результаты сохраняются как historical-only evidence без изменения текущей цели. Неизвестные часы/регресс времени также не дают live-подтверждения; future observation отвергается на приёме dispatcher.

Взаимные peer health проверки сохраняются для всех доверенных узлов. `PeerObservationCapture` связывает результат с проверенным NodeId; node target отличается от server и использует `NodeChatRoute`. Network timeout означает только PeerUnavailable. Сообщённые DatabaseUnavailable, WorkerStalled и TelegramUnavailable сохраняются отдельно; ошибка отдельного чата не входит в эти health facts. Для узлов используются 5 с/3 с и шесть сетевых ошибок, сообщённая компонентная неисправность подтверждается сразу. Реестр NodeId/storage generation приходит только из trusted RecoveryAdmission и должен совпасть с восстановленными ClusterMember; его provisioning принадлежит 09.

`MonitoringAvailability` использует принятые EvaluationTimeUtc/frontier. Для каждого назначенного observer порог ошибок считается независимо; success сбрасывает последовательность. Свежесть равна `2 * interval + timeout + 20 секунд` (150 с при 60/10). Имя и время запуска сохраняются отдельно по observers; различия metadata не являются отказом. Available/Partial/Unavailable/Unknown отделены от Complete/Initializing/Degraded/Lost покрытия. Missing/stale не доказывает outage и не закрывает открытую аварию; recovery цели требует свежего успеха всех назначенных направлений.

`MonitoringCoordinator` обрабатывает historical watermark всех назначенных observers до live head: до 10 временных точек цели и 100 команд прохода с ротацией целей. Неполный исторический диапазон не перескакивает в live. Неизменный frontier переоценивается не чаще пяти секунд для истечения freshness. Уже принятая история не переписывается поздним evidence. Нового главного подтверждают существующие term/leadership/catch-up gates; обычный partial не вызывает resign.

Incident/event/intents и cursor/receipt входят в один существующий projection write set. Coverage имеет собственные события и получателей. Стабильные IDs, порядок и dedup сохраняются при replay; аудит пишет только подтверждённые новые переходы с системным source и безопасными IDs. Получатели recovery требуют принятого доказательства настоящей успешной попытки; Planned intent не является доставкой.

До состава backlog требуется восстановленная история и независимое DeliveryStateConfirmed. Завершённые неуведомлённые аварии входят в durable summaries без отсечения по возрасту; открытые и обычный recovery ранее уведомлённых чатов сохраняются отдельно. Исходные events/intents остаются, источники сводки получают SuppressedBySummary. Поздняя порция backlog в том же term создаёт новый состав. Производитель attempts и dispatcher подключены в 07; реальные внешние отправки не проверялись.

`MonitoringDiagnostics` возвращает только index, число streams/незавершённых historical assemblies, время/возраст и safe reason. Полное покрытие не объявляется при недогоне или отсутствующих целях. Safe UI показывает availability отдельно от coverage и metadata каждого observer. Часовой догон при трёх узлах/около 30 приложений остаётся неизмеренной целью самостоятельного наблюдения, без обязательной нагрузки. Результаты и пределы — [Testing.md](Testing.md#проверка-этапа-09).

## Режим сетки после этапа 06

Ниже описан однопроцессный путь при `Cluster:Mode=Disabled`. В enabled-режиме прежний `MonitoringWorker` не регистрируется, прежние browser writers закрыты. Распределённые наблюдения/инциденты реализованы в 06 за закрытыми readiness gates. `MonitoringProgressWatchdog` получает подтверждение завершённого цикла; отсутствие свежего прогресса держит sender закрытым. Собственная неисправность БД закрывает CanWork, новые native ACK/vote и sender, supervisor останавливает Raft, не уменьшая состав. После ремонта нужен подтверждённый replay/catch-up. Обычная partial-цель не участвует в health-quorum выбора главного. Возраст health и окно 30 с описаны в [Cluster runtime.md](Cluster%20runtime.md).

Принятые настройки 04 имеют отдельные ConfigRevision/CheckGeneration: endpoint/enable change требует нового поколения, metadata/родительская связь сохраняет его; tombstone не позволяет старой настройке воскресить цель. Production projection update сохраняет monitoring-поля. Распределённые observations/incidents принимает тот же reducer; регистрация worker не открывает activation/delivery gates.

## Состояние реализации

Этап движка мониторинга реализован. Application содержит независимые server/container contracts, результаты ожидаемых endpoint-ошибок, `MonitoringService` и `MonitoringStateChangedEvent`. Infrastructure содержит HTTP/JSON-адаптер, запуск цикла и hosted worker.

## Контракты endpoint

Проверка выполняется HTTP GET без авторизации.

Ответ сервера и контейнера должен быть успешным JSON-документом с непустым `name` и обязательным `startedAtUtc` в UTC. Для сервера это время загрузки ОС, для контейнера — время запуска приложения. Время запуска не может находиться в будущем относительно времени проверки.

```json
{"name":"ubuntu-server-01","startedAtUtc":"2026-11-01T03:20:00Z"}
```

`Z` и `+00:00` обозначают нулевой часовой пояс и принимаются одинаково. Дата без явного часового пояса, с ненулевым смещением или отсутствующим полем даёт `InvalidStartedAtUtc`. Прежний серверный ответ только с `name` больше не является успешным. [Подготовка Ubuntu с nginx](Ubuntu%20server%20monitoring.md) описывает открытый endpoint без ограничения по IP и настройки UFW. [Подключение контейнера ASP.NET Core](ASP.NET%20Core%20container%20monitoring.md) содержит пример endpoint приложения и порядок добавления в проект.

Имя trim-ится, обязано быть непустым и не превышать `ModelConstraints.OBJECT_NAME_MAX_LENGTH`. `startedAtUtc` обязан быть JSON-строкой с нулевым UTC offset и не превышать время завершения проверки.

Неуспешный HTTP status, timeout, сетевое исключение, превышение размера, некорректный JSON или неверное обязательное поле преобразуются в `EndpointCheckFailureKind` и считаются неудачной проверкой одного объекта. Неожиданные исключения не маскируются таким результатом: цикл изолирует их на границе объекта и логирует.

Автоматические redirect отключены, поэтому любой `3xx`, включая redirect на другой host и loop, считается HTTP-ошибкой. Максимальный response body равен 64 KiB: сначала проверяется `Content-Length`, затем обычное, chunked или streamed тело читается только в буфер `limit + 1`. URL и body ответа не включаются в исключения и логи.

## Цикл

- Каждые 30 секунд выбираются включённые объекты.
- Разные объекты могут проверяться параллельно.
- Для одного объекта одновременно допускается только одна проверка.
- Внутри цикла нет немедленного retry.
- Ошибка одного объекта не завершает обработку остальных.
- Штатная остановка передаётся через `CancellationToken`.

`MonitoringWorker` запускает первый цикл сразу, а следующие — по `PeriodicTimer` с интервалом из `MonitoringOptions`. Каждый цикл заново получает no-tracking список включённых серверов и контейнеров. HTTP-клиент создаётся через `IHttpClientFactory`, выполняет анонимный GET и ограничивается timeout из тех же options. Текущее время и timer получают `TimeProvider`.

`MonitoringCycleRunner` запускает разные цели параллельно. Каждая цель получает отдельный DI scope и `IMonitoringUnitOfWork`, поэтому параллельные задачи не разделяют EF `DbContext`. Keyed in-memory coordination по типу и GUID пропускает повторный запуск уже активной цели и освобождается в `finally`. Retry внутри цикла отсутствует.

## Сохранение результата

`MonitoringService` после HTTP-проверки заново загружает tracked объект через `IMonitoringUnitOfWork`. Результат игнорируется, если объект за время запроса удалён, выключен или его endpoint изменён. Иначе use case вызывает существующий доменный переход, добавляет outage/recovery payload в durable outbox и одним `SaveChangesAsync` сохраняет состояние и job.

`MonitoringStateChangedEvent` содержит тип и ID объекта, parent server ID контейнера, описание, внутреннее имя из endpoint, status, transition, failure counter, времена, успешность и семантическую endpoint-ошибку. Административное имя сервера проверка не изменяет. `MonitoringEventPublisher` передаёт событие внутренним handlers только после успешного сохранения.

Последнее успешное `StartedAtUtc` сервера сохраняется доменным методом и передаётся в событие; ошибка его не стирает, смена endpoint очищает. Cluster server capture включает это же время в `MonitoringObservation`, поэтому существующие per-observer metadata и uptime доступны для серверов. Legacy dashboard проецирует серверный `StartedAtUtc` и рассчитывает `UptimeSeconds` по времени snapshot; при неизвестном запуске оба значения равны `null`.

SignalR handler реализован в Web. Он получает из Application список разрешённых включённых пользователей и отправляет им только `objectId` и `objectKind`; скрытые имя, описание, состояние и времена в push payload не попадают. Telegram не является synchronous handler: отдельный Infrastructure worker читает durable outbox. SQL Server остаётся источником истины.

`ObjectVisibilityPolicy` предоставляет доступ только активному Administrator ко всем объектам, включая контейнеры без сервера. Старые permission-наборы не предоставляют доступ. Infrastructure `IMonitoringRecipientReadQuery` читает users/роль одним запросом и возвращает пустые compatibility collections; per-user queries и рабочих ACL нет. В enabled-режиме `AcceptedMonitoringRecipients` читает активных принятых Administrator без ресурсных ACL, `MonitoringConnections` проверяет локальную cookie-сессию перед доставкой и отзывает подписку после apply hash/stamp/disable/delete/fault. Это не открывает readiness gates распределённого worker.

## Конкурентность

Однопроцессная первая версия использует keyed in-memory coordination. Дополнительно `MonitoredServer.Version` обоих типов Server / Application являются optimistic concurrency token. Если административное изменение фиксируется после повторной проверки объекта, stale monitoring write получает `DbUpdateConcurrencyException`, переводится в `PersistenceConcurrencyException`, очищает tracking и не публикует SignalR-событие или Telegram job. Stale административный request возвращает `409 Conflict`.

Распределённые checks реализованы отдельно от этого Disabled-пути; sender/attempt lifecycle подключён в 07 за общим gate.
