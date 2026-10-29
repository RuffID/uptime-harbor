# Движок мониторинга

## Режим сетки после этапов 03/04

Ниже описан однопроцессный путь при `Cluster:Mode=Disabled`. В enabled-режиме прежний `MonitoringWorker` не регистрируется, прежние browser writers закрыты. Распределённые наблюдения/инциденты принадлежат 06. `MonitoringProgressWatchdog` предоставляет будущему worker контракт подтверждения завершённого цикла; отсутствие свежего прогресса держит sender закрытым. Собственная неисправность БД закрывает CanWork, новые native ACK/vote и sender, supervisor останавливает Raft, не уменьшая состав. После ремонта нужен подтверждённый replay/catch-up. Обычная partial-цель не участвует в health-quorum выбора главного. Возраст health и окно 30 с описаны в [Cluster runtime.md](Cluster%20runtime.md).

Принятые настройки 04 имеют отдельные ConfigRevision/CheckGeneration: endpoint/enable change требует нового поколения, metadata/родительская связь сохраняет его; tombstone не позволяет старой настройке воскресить цель. Production projection update сохраняет monitoring-поля. Само вычисление распределённых observations/incidents не реализовано и не включается наличием reducer.

## Состояние реализации

Этап движка мониторинга реализован. Application содержит независимые server/container contracts, результаты ожидаемых endpoint-ошибок, `MonitoringService` и `MonitoringStateChangedEvent`. Infrastructure содержит HTTP/JSON-адаптер, запуск цикла и hosted worker.

## Контракты endpoint

Проверка выполняется HTTP GET без авторизации.

Ответ сервера должен быть успешным JSON-документом с непустым `name`.

Ответ контейнера должен дополнительно содержать `startedAtUtc` в UTC. Время запуска не может находиться в будущем относительно времени проверки.

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

SignalR handler реализован в Web. Он получает из Application список разрешённых включённых пользователей и отправляет им только `objectId` и `objectKind`; скрытые имя, описание, состояние и времена в push payload не попадают. Telegram не является synchronous handler: отдельный Infrastructure worker читает durable outbox. SQL Server остаётся источником истины.

`ObjectVisibilityPolicy` предоставляет доступ только активному Administrator ко всем объектам, включая контейнеры без сервера. Старые permission-наборы не предоставляют доступ. Infrastructure `IMonitoringRecipientReadQuery` читает users/роль одним запросом и возвращает пустые compatibility collections; per-user queries и рабочих ACL нет. В enabled-режиме `AcceptedMonitoringRecipients` читает активных принятых Administrator без ресурсных ACL, `MonitoringConnections` проверяет локальную cookie-сессию перед доставкой и отзывает подписку после apply hash/stamp/disable/delete/fault. Это не открывает workers этапа 06.

## Конкурентность

Однопроцессная первая версия использует keyed in-memory coordination. Дополнительно `MonitoredServer.Version` и `MonitoredContainer.Version` являются optimistic concurrency token. Если административное изменение фиксируется после повторной проверки объекта, stale monitoring write получает `DbUpdateConcurrencyException`, переводится в `PersistenceConcurrencyException`, очищает tracking и не публикует SignalR-событие или Telegram job. Stale административный request возвращает `409 Conflict`.

Если появится несколько экземпляров приложения, потребуется отдельное распределённое решение для запуска checks и claim outbox jobs, но оно не входит в первую версию.
