# Результаты технического ревью первой версии

> Историческая оговорка: после этого ревью persistence переведён с PostgreSQL на Microsoft SQL Server и создан baseline `20260831015604_InitialSqlServerSchema`. Упоминания PostgreSQL, `xmin`, прежних migration и PostgreSQL health probe ниже относятся к состоянию на момент ревью; они не являются текущей рекомендацией и не подтверждают SQL Server runtime-проверку.

## Область проверки

Проверка выполнена относительно `Documentation/Business logic/Business requirements.md` и текущей технической документации. В область проверки вошли Domain, Application, Infrastructure, Web, TypeScript-клиент, persistence, monitoring worker, Telegram-доставка, тесты и deployment-материалы.

Разрешённые Release-сборки и изолированные тесты повторно выполнены после P3-рефакторинга: Domain 16, Application 91, Infrastructure 61 и Web 51 тест прошли. Docker, Web-host, browser automation, PostgreSQL, SQL, миграции, npm, LibMan и проектные скрипты не запускались. Provider-level проверки выполнены только на SQLite in-memory.

## Итог

P0-1 и P0-2 устранены, P0-3 исключён из v1; статусы и гарантии P0/P1 не ослаблялись. P2-1—P2-5 устранены в исходном коде и разрешённом изолированном наборе, при этом browser burst/cancellation, CSP/SignalR runtime и настоящий HTTP input formatter pipeline остаются ручными runtime-проверками. P3-1—P3-3 устранены полностью как внутренний серверный рефакторинг без изменения продуктовых контрактов. До ручных P1/P2 runtime-проверок и применения migration версию нельзя считать runtime-ready.

## Блокирующие проблемы

### P0-1. Страница администрирования объектов ломается из-за несовпадения JSON- и TypeScript-контрактов

**Статус исправления: устранено и проверено.** TypeScript использует числовой `MonitoringStatus`, а Web-тест сериализует административные DTO через фактические MVC JSON options. Повторные Debug-сборки Web-проекта проходят без ошибки Static Web Assets.

`AdminServerDto.Status` и `AdminContainerDto.Status` имеют тип `MonitoringStatus`. MVC не настраивает `JsonStringEnumConverter`, поэтому `System.Text.Json` по умолчанию возвращает enum числом. При этом `wwwroot/ts/pages/admin/objects.ts` ожидает строки `"Unknown"`, `"Available"` и `"Unavailable"`, а `statusText` падает для числового значения.

Следствие: после появления первого объекта загрузка списка вызывает ошибку `Неизвестный статус`, и основной административный сценарий становится неработоспособным.

Затронутые места:

- `UptimeHarbor.Application/ObjectAdministration/Models/AdminServerDto.cs:11`;
- `UptimeHarbor.Application/ObjectAdministration/Models/AdminContainerDto.cs:12`;
- `UptimeHarbor.Web/Pages/ServiceCollectionExtensions.cs:15`;
- `UptimeHarbor.Web/wwwroot/ts/pages/admin/objects.ts:10`;
- `UptimeHarbor.Web/wwwroot/ts/pages/admin/objects.ts:456`.

Рекомендуемое решение: оставить единый числовой Web-контракт, как уже сделано на dashboard, и заменить строковый TypeScript union на числовой enum. Глобальное включение строковой сериализации менее предпочтительно, потому что изменит остальные существующие Web-контракты.

Обязательная проверка: сериализовать реальный `JsonResult` через MVC JSON options и проверить payload административных DTO, затем статически или браузерным smoke-тестом пройти создание, повторную загрузку и редактирование сервера и контейнера.

### P0-2. Нет пользовательского интерфейса управления пользователями и видимостью

**Статус исправления: устранено и проверено.** Добавлены AJAX-only страница и TypeScript-модуль управления обычными пользователями, permissions и POST-выход; self-disable/self-delete защищены на сервере. Application и Web-тесты проходят.

`AdminUsersController` и application service реализованы, но в `Pages/Admin` и `wwwroot/ts/pages/admin` отсутствует страница/модуль пользователей. В навигации `_Layout.cshtml` также нет такого раздела. Администратор не может через интерфейс создать, заблокировать или удалить пользователя и назначить серверы/контейнеры, хотя это является критерием готовности v1.

Дополнительно в общей навигации нет доступной пользователю команды выхода: страница `/Account/Logout` существует, но ссылка или POST-форма на неё отсутствует.

Затронутые места:

- `UptimeHarbor.Web/Controllers/AdminUsersController.cs`;
- `UptimeHarbor.Web/Pages/Shared/_Layout.cshtml`;
- отсутствующие `UptimeHarbor.Web/Pages/Admin/Users.cshtml` и `UptimeHarbor.Web/wwwroot/ts/pages/admin/users.ts`;
- `Documentation/Plans/application-implementation-2026-08-04-(completed)/03 - Authentication and visibility.md:5`, где этап уже отмечен завершённым.

Рекомендуемое решение: добавить AJAX-only административную страницу пользователей на TypeScript с созданием, блокировкой, удалением и атомарной заменой permissions. Контейнеры должны становиться доступными для выбора только вместе с родительским сервером. В layout добавить POST-выход. До реализации перевести этап 3 и общий план в статус `В работе`.

Обязательная проверка: browser/Web smoke-сценарий «создать пользователя → назначить сервер и один контейнер → войти этим пользователем → проверить скрытые объекты → заблокировать → убедиться в немедленной потере доступа → удалить».

### P0-3. Deployment/release-контур не завершён

**Статус исправления: исключено из объёма v1.** Требования `Documentation/Business logic/Business requirements.md` не включают deployment orchestration; добавленные ранее `compose.yaml` и каталог `deploy` удалены. История замечания и рекомендации сохранены для возможного последующего этапа.

Этап 8 отмечен как `Приостановлен`: Docker Compose и orchestration намеренно исключены из первой версии. Отсутствие Compose не является дефектом ТЗ v1; этот абзац сохраняет исходное содержание замечания для истории ревью.

Кроме того, Web не вызывает `UseForwardedHeaders`. За TLS-terminating reverse proxy `UseHttpsRedirection` будет принимать решение по внутренней HTTP-схеме. Это создаёт риск циклических HTTPS-redirect и некорректного определения исходной схемы/host. `/health` содержит только пустой стандартный health check и не отражает готовность PostgreSQL.

Затронутые места:

- `Documentation/Plans/application-implementation-2026-08-04-(completed)/08 - Deployment and release.md:5`;
- `Documentation/Technical documentation/Deployment.md:7`;
- `UptimeHarbor.Web/Program.cs:36`;
- `UptimeHarbor.Web/Program.cs:46`;
- `UptimeHarbor.Web/Pages/ServiceCollectionExtensions.cs:23`.

Рекомендуемое решение на момент ревью: завершить deployment/release-контур отдельным этапом. Эта рекомендация не является обязательной работой первой версии после уточнения границ ТЗ.

Обязательная проверка: чистое развёртывание, миграция пустой БД, вход и SignalR через Nginx, перезапуск/пересоздание Web и PostgreSQL-контейнеров без потери данных, контролируемая недоступность БД и восстановление.

## Высокий приоритет

### P1-1. Результат старой проверки может перезаписать административное изменение

**Статус исправления: устранено в коде и provider tests; PostgreSQL runtime-проверка остаётся ручной.** `MonitoredServer` и `MonitoredContainer` получили явный `Version` concurrency token. Infrastructure переводит `DbUpdateConcurrencyException` в независимый `PersistenceConcurrencyException` и очищает stale tracking. Monitoring возвращает ожидаемый ignored result без SignalR/outbox event; административный DTO/request передаёт version и получает `409 Conflict`. SQLite provider tests используют два независимых DbContext для disable, endpoint change и двух admin updates. Добавлена минимальная migration `AddMonitoringConcurrencyAndNotificationOutbox`; она не применялась.

`MonitoringService` повторно загружает объект и проверяет enabled/endpoint перед изменением, но между этой проверкой и `SaveChangesAsync` другой scope может выключить мониторинг или заменить endpoint. Для monitored entities не настроен concurrency token. В результате старая проверка способна записать `Available`, имя и времена уже выключенному объекту либо объекту с новым endpoint.

Keyed coordination защищает только две monitoring-проверки одного объекта и не координируется с административными запросами.

Затронутые места:

- `UptimeHarbor.Application/Monitoring/MonitoringService.cs:40-54`;
- `UptimeHarbor.Application/Monitoring/MonitoringService.cs:69-84`;
- `UptimeHarbor.Infrastructure/Persistence/Configurations/MonitoredObjectConfiguration.cs`, где отсутствует concurrency token.

Рекомендуемое решение: добавить оптимистическую конкурентность для серверов и контейнеров, предпочтительно через PostgreSQL `xmin`/явный version token. `MonitoringService` должен считать `DbUpdateConcurrencyException` ожидаемым устаревшим результатом, очистить tracking и не публиковать событие. Административный API должен возвращать `409 Conflict` при конфликтующем редактировании.

Обязательная проверка: provider-level тесты двух независимых DbContext для гонок «проверка vs disable», «проверка vs endpoint change» и «два административных update».

### P1-2. Единственный администратор может заблокировать систему

**Статус исправления: устранено и проверено.** Self-disable/self-delete возвращают существующий `ServiceResult/ServiceError`; отключение и удаление последнего enabled administrator возвращает `Conflict`. Identity store повторяет guard транзакционно и использует concurrency stamp общей роли для конкурирующих операций. Bootstrap при единственном disabled administrator включает существующую запись без дубликата и смены пароля. UI скрывает/отключает очевидные действия для текущего администратора.

`UserManagementService.SetEnabledAsync` и `DeleteAsync` не запрещают текущему администратору отключить или удалить самого себя. Это особенно опасно, потому что UI/API не умеют создавать второго администратора. `AdministratorExistsAsync` считает отключённого администратора существующим, поэтому bootstrap после перезапуска не создаст доступную административную запись.

Затронутые места:

- `UptimeHarbor.Application/Users/UserManagementService.cs:84-129`;
- `UptimeHarbor.Infrastructure/Identity/IdentityUserAccountStore.cs:76-126`.

Рекомендуемое решение: запретить self-disable/self-delete по `accessContext.UserId`, а на уровне store/use case не разрешать отключение или удаление последнего включённого администратора. Если управление несколькими администраторами не входит в v1, административную запись вообще не следует показывать как блокируемую/удаляемую.

Обязательная проверка: тесты self-disable, self-delete, last-enabled-admin и bootstrap при наличии только отключённого администратора.

### P1-3. Telegram-доставка синхронно задерживает monitoring worker

**Статус исправления: устранено в коде и изолированных тестах; process restart и Telegram runtime остаются ручными.** Outage/recovery payload сохраняется в durable outbox одной транзакцией с monitoring state. Отдельный hosted worker claim-ит jobs с lease, обрабатывает до четырёх jobs и четырёх получателей параллельно и не участвует в monitoring cycle. Retry ограничен пятью попытками с backoff 5 секунд, 30 секунд, 2 минуты и 5 минут. SQLite/fake tests подтверждают сохранность job, новый DbContext, независимость следующего monitoring cycle и начало доставки другого чата при зависшем transport.

После сохранения результата `MonitoringService` ожидает `MonitoringEventPublisher`. Telegram handler последовательно отправляет сообщения каждому чату, а timeout одного вызова равен 30 секундам. Пока отправки не завершены, задача объекта остаётся частью `Task.WhenAll` текущего цикла; следующий monitoring cycle не запускается. Несколько медленных чатов способны задержать проверки всех объектов на минуты.

Это противоречит требованиям о регулярных независимых проверках и о том, что ошибка одного чата не должна мешать мониторингу и другим чатам.

Затронутые места:

- `UptimeHarbor.Application/Monitoring/MonitoringEventPublisher.cs:8-16`;
- `UptimeHarbor.Infrastructure/Telegram/TelegramMonitoringEventHandler.cs:41-54`;
- `UptimeHarbor.Infrastructure/ServiceCollectionExtensions.cs:118`.

Рекомендуемое решение: после транзакции мониторинга записывать durable notification jobs/outbox и завершать проверку объекта. Отдельный background dispatcher должен читать задания, ограниченно параллелить получателей, применять retry/backoff и сохранять результат доставки. Простого `Task.Run` недостаточно: задания будут теряться при остановке процесса.

Обязательная проверка: зависший Telegram transport не задерживает начало следующего monitoring cycle; перезапуск между созданием задания и отправкой не теряет уведомление; ошибка одного чата не задерживает остальные сверх заданного bounded concurrency.

### P1-4. Гарантия «одно Telegram-сообщение» технически не выполняется

**Статус исправления: устранено согласованием контракта.** `Documentation/Business logic/Business requirements.md`, техническая документация и тестовые ожидания теперь фиксируют at-least-once. Durable outbox исключает потерю локального job, но не скрывает окно после принятия `sendMessage` и до локального marker; редкий дубликат явно допускается. Exactly-once больше не заявляется.

Текущий поток сначала вызывает Telegram, затем в другом scope сохраняет marker успешной доставки. При падении процесса или БД после `sendMessage`, но до `MarkDeliverySuccessfulAsync`, следующий цикл отправит то же сообщение повторно. Обратный порядок привёл бы к потере сообщения. Telegram Bot API не предоставляет idempotency key для `sendMessage`, поэтому строгий exactly-once контракт таким способом недостижим.

Затронутое место: `UptimeHarbor.Infrastructure/Telegram/TelegramMonitoringEventHandler.cs:45-54`.

Рекомендуемое решение: принять и явно задокументировать at-least-once семантику с редкими дубликатами либо изменить внешний transport/посредника на поддерживающий идемпотентность. Durable outbox существенно уменьшит окно сбоев и исключит потерю задания, но сам по себе не докажет exactly-once Telegram-доставку. Формулировку `Documentation/Business logic/Business requirements.md` и тесты нужно привести к реально обеспечиваемой гарантии.

### P1-5. Monitoring HTTP adapter не ограничивает размер ответа и следует redirect по умолчанию

**Статус исправления: устранено и проверено.** Максимум response body — 64 KiB. Adapter проверяет `Content-Length`, ограничивает обычное/chunked/streamed чтение буфером `limit + 1`, возвращает `ResponseTooLarge`, сохраняет cancellation/timeout и не включает body/URL в ошибки. Named client явно использует `AllowAutoRedirect = false`, а любой `3xx` считается `HttpStatus`. Fake transport tests покрывают oversized варианты, redirects, cancellation и timeout чтения.

Ответ читается и разбирается потоково, но предельный размер JSON отсутствует. Скомпрометированный или ошибочно настроенный endpoint может вернуть большой/бесконечный ответ и создать высокий расход памяти/CPU сразу для множества параллельных проверок. Автоматические redirect также позволяют исходно разрешённому адресу перенаправить запрос на другой, в том числе внутренний, адрес.

Затронутое место: `UptimeHarbor.Infrastructure/Monitoring/HttpMonitoringEndpointChecker.cs`.

Рекомендуемое решение: ввести небольшой документированный maximum response size, проверять `Content-Length`, читать через ограничивающий stream и отклонять превышение отдельным failure kind. Настроить redirect policy явно; рекомендуемый вариант — отключить redirect и считать 3xx неуспехом. Для production ограничить egress сети Web-контейнера и определить допустимость private/link-local/loopback endpoint согласно реальной схеме эксплуатации.

Обязательная проверка: oversized body, chunked oversized body, redirect на другой host, redirect loop и cancellation во время чтения.

### P1-6. Тестовый набор не проверяет реальные Web-контракты и критические гонки

**Статус исправления: устранено в разрешённом изолированном наборе; внешние runtime smoke остаются ручными.** MVC contract tests используют фактические `JsonOptions`; ServiceResult/HTTP mapping включает `409`; optimistic concurrency и отсутствие events проверяются реальным SQLite provider с двумя DbContext; security, outbox/retry и HTTP limits/redirect закреплены тестами. PostgreSQL migration/concurrency, реальный process restart, Telegram API и browser smoke не запускались согласно ограничениям задачи. Docker/Nginx/Compose не являются обязательным P1-тестом v1 после исключения P0-3.

`JsonResultMapperTests` проверяет объект `JsonResult.Value`, но не прогоняет его через фактические MVC JSON options. Поэтому несовпадение enum-контракта осталось незамеченным. Большинство Web-проверок являются metadata/static tests; отсутствует smoke-проверка критических пользовательских потоков. Persistence tests проверяют metadata, но не конфликт двух реальных DbContext и не release-миграцию PostgreSQL.

Рекомендуемое решение:

1. добавить Web contract tests с реальной MVC-сериализацией для каждого DTO, используемого TypeScript;
2. добавить статическую проверку соответствия числовых enum и TypeScript-контрактов;
3. добавить provider-level concurrency tests;
4. вынести Docker/PostgreSQL/Nginx/browser проверки в отдельный release smoke suite, не смешивая их с изолированными unit tests;
5. не отмечать этап завершённым без прохождения соответствующего smoke-сценария.

## Средний приоритет

### P2-1. SignalR создаёт всплеск полных snapshot-запросов

**Статус исправления: устранено в коде и изолированных проверках; browser runtime-проверка остаётся ручной.** Dashboard использует документированное окно coalescing 200 мс, новый snapshot отменяет предыдущий fetch через `AbortController`, а `AbortError` не отображается как ошибка. Reconnect по-прежнему немедленно загружает snapshot. Recipient resolver сохраняет `ObjectVisibilityPolicy`, загружает пользователей один раз и permissions пакетно без per-user N+1. SignalR payload остаётся парой `objectId`/`objectKind`. Фактический сценарий «30 быстрых событий → ограниченное число requests» не запускался без запрещённых Web-host/browser/JS harness и не подменён статическим тестом.

Событие публикуется после каждой проверки каждого объекта, включая проверки без смены статуса. На каждое событие dashboard немедленно вызывает `loadSnapshot(false)`. При целевых 30 объектах один цикл способен создать около 30 полных HTTP snapshot-запросов на каждый открытый dashboard. Старые запросы не отменяются: `requestVersion` только запрещает им перерисовать UI.

Дополнительно `MonitoringEventRecipientResolver` для каждого события получает всех пользователей и отдельно загружает permissions каждого пользователя, что умножает N+1 запросы на число объектов.

Затронутые места:

- `UptimeHarbor.Application/Monitoring/MonitoringService.cs:55,85`;
- `UptimeHarbor.Web/wwwroot/ts/pages/dashboard.ts:101-113`;
- `UptimeHarbor.Application/Visibility/MonitoringEventRecipientResolver.cs:14-70`.

Рекомендуемое решение: debounce/coalesce snapshot reload на клиенте и отменять предыдущий fetch через `AbortController`. На сервере загружать пользователей и permissions пакетно; ещё лучше публиковать один обезличенный refresh-сигнал на пользователя по завершении набора изменений, сохраняя server-side visibility.

Обязательная проверка: 30 событий за короткий интервал приводят к одному или ограниченному числу snapshot-запросов.

### P2-2. Обычным пользователям раскрываются endpoint URL через неиспользуемое API

**Статус исправления: устранено и проверено.** Подтверждённых v1-потребителей `/api/objects` нет, поэтому controller, `AccessibleObjectsService`, его interface/DTO и отдельные тесты удалены. Dashboard/read DTO не содержат endpoint URL или monitoring settings, скрытые объекты фильтруются прежним `DashboardQuery`, а `/api/admin/objects` сохраняет endpoint URL для редактирования. Web-тест проверяет отсутствие route `api/objects` во всех controller types.

`/api/objects` доступен любому аутентифицированному пользователю и возвращает `EndpointUrl` в `AccessibleServerDto`/`AccessibleContainerDto`. Главная страница этот API не использует, а требования обычного пользователя предусматривают имя, описание, статус и времена, но не адрес внутренней проверки.

Затронутые места:

- `UptimeHarbor.Web/Controllers/ObjectsController.cs:10-36`;
- `UptimeHarbor.Application/Visibility/Models/AccessibleServerDto.cs:7`;
- `UptimeHarbor.Application/Visibility/Models/AccessibleContainerDto.cs:8`.

Рекомендуемое решение: удалить неиспользуемый controller/service, если для v1 нет потребителя. Если прямое чтение требуется, ввести отдельные read DTO без endpoint и других административных полей.

### P2-3. Сохранённое административное изменение может завершиться HTTP-ошибкой из-за SignalR

**Статус исправления: устранено и проверено.** Сохранение по-прежнему выполняется первым. Web publisher best-effort изолирует SignalR exception и журналирует общую ошибку без объекта/endpoint; сохранённый use case возвращает success без повторного HTTP-запроса. Тесты подтверждают один refresh на успешном пути, отсутствие вызова после save failure/concurrency conflict и успешный результат commit при исключении SignalR. Telegram outbox не используется.

`ObjectAdministrationService` сначала сохраняет данные, затем ожидает `IObjectAdministrationChangePublisher`. Web-реализация не изолирует ошибку SignalR. При сбое push клиент получит ошибку операции, хотя данные уже сохранены, и повтор может создать дубликат или вызвать неожиданный конфликт.

Затронутые места:

- `UptimeHarbor.Application/ObjectAdministration/ObjectAdministrationService.cs`;
- `UptimeHarbor.Web/Hubs/SignalRObjectAdministrationChangePublisher.cs`.

Рекомендуемое решение: сделать refresh-сигнал best-effort и изолировать/логировать его ошибку после commit либо использовать outbox, если доставка такого сигнала должна быть надёжной. HTTP-результат не должен сообщать об откате уже зафиксированного изменения.

### P2-4. Единый JSON envelope не действует для ошибок model binding

**Статус исправления: устранено в зарегистрированном MVC-контракте; полный formatter runtime остаётся ручным.** `InvalidModelStateResponseFactory` использует `ServiceResult.Validation` и `JsonResultMapper`, возвращая HTTP 400 и `{ success: false, message, field? }`. Выбор первого поля детерминирован, field переводится в camelCase, parser exception message/CLR type не возвращаются. Реально зарегистрированная factory и MVC JSON options проверены для malformed body, missing body, wrong type и validation field. TypeScript helper безопасно различает JSON, empty/non-JSON HTTP errors и нативный `AbortError`, а неожиданный пустой success остаётся fail-fast. Настоящий input formatter без Web-host не запускался и не заменён статическим поиском.

Контроллеры помечены `[ApiController]`. Ошибки разбора JSON и автоматической валидации до входа в action будут возвращаться стандартным `ValidationProblemDetails`, а не документированным `{ success: false, message, field? }`. TypeScript helper предполагает собственный envelope и при другом теле выдаёт неуправляемую ошибку парсинга/общее сообщение.

Рекомендуемое решение: настроить `InvalidModelStateResponseFactory` на проектный envelope и сделать `sendJsonRequest` fail-fast, но предсказуемо обрабатывать не-JSON/пустые ответы. Закрепить malformed JSON, отсутствующее body и неверный тип поля контрактными тестами.

### P2-5. Защита Web-периметра неполна

**Статус исправления: устранено в коде и изолированных проверках; ручная browser/runtime-проверка остаётся.** `lockoutOnFailure` включён: пять ошибок дают временный lockout на пять минут, успех до порога сбрасывает failures, `IsEnabled` не смешивается с lockout, bootstrap не снимает lockout и не создаёт второго администратора. Login имеет отдельный лимит 10 запросов в минуту на доверенный connection IP и безопасный 429; остальные API его не получают. Добавлены `nosniff`, `no-referrer`, `X-Frame-Options: DENY` и применяемая same-origin CSP без unsafe/wildcard/CDN origins. SignalR browser asset поставляется локально в версии 10.0.11 из `wwwroot/vendor/signalr/signalr.min.js`, подключается с cache busting; SHA-256 зафиксирован в технической документации. Исходное описание прежнего блокера и ограничений сохранено ниже.

Вход выполняется с `lockoutOnFailure: false`, rate limiting отсутствует. SignalR client загружается с внешнего CDN без `integrity`; Content Security Policy не настроена. Недоступность или подмена внешнего ресурса ломает realtime-обновления dashboard.

Затронутые места:

- `UptimeHarbor.Infrastructure/Identity/UserSignInService.cs`;
- `UptimeHarbor.Web/Pages/Index.cshtml:47-48`;
- `UptimeHarbor.Web/Program.cs`.

Рекомендуемое решение: включить ограничение частоты входа и контролируемый Identity lockout, локально поставлять фиксированную версию SignalR client, добавить CSP и базовые security headers. Для lockout предусмотреть понятный операционный способ восстановления администратора.

## Рефакторинг после исправления функциональных дефектов

### P3-1. Дублируется авторизация администратора

Одинаковая логика `IsAuthenticated`/`IsEnabled`/`IsAdministrator` повторяется в `UserManagementService`, `ObjectAdministrationService` и `NotificationAdministrationService`. Формулировки ошибок и будущие правила могут разойтись.

Рекомендуемое решение: выделить небольшой независимый Application policy/service, возвращающий единый `ServiceError?`. Не переносить проверку только в controller: защищаемый use case должен продолжать проверять доступ сам.

**Статус: устранён полностью.** Добавлен независимый `IAdministratorAccessPolicy`, использующий только `IUserAccessContext` и semantic `ServiceError`. `UserManagementService`, `ObjectAdministrationService` и `NotificationAdministrationService` вызывают policy внутри каждого защищённого use case до I/O и side effects; controller role metadata, self/last-admin guards и best-effort SignalR после commit сохранены.

### P3-2. Контракты репозиториев возвращают изменяемые `List<T>`

Application-интерфейсы спискового чтения возвращают `Task<List<T>>`, хотя потребителям не требуется изменять коллекцию и проектные правила требуют read-only materialized results.

Рекомендуемое решение: после более срочных исправлений заменить внешние контракты на `IReadOnlyList<T>` и оставить конкретный `List<T>` деталью Infrastructure/materialization. Не выполнять это одновременно с функциональными исправлениями конкурентности.

**Статус: устранён полностью.** Все списковые read-методы Application persistence contracts возвращают `Task<IReadOnlyList<T>>`. Infrastructure по-прежнему materialize-ит результаты в `List<T>` через list operations/`ToListAsync`; `IQueryable` и EF-типы не вышли за пределы Infrastructure, write-range контракты и схема БД не менялись.

### P3-3. Visibility/read-query логика размножена

`AccessibleObjectsService`, `DashboardQuery` и `MonitoringEventRecipientResolver` отдельно загружают объекты/permissions и повторяют orchestration фильтрации. Это уже привело к лишнему `/api/objects` и N+1 запросам.

Рекомендуемое решение: оставить `ObjectVisibilityPolicy` чистым правилом, но добавить специализированные materialized query-контракты для dashboard и recipient resolution с пакетной загрузкой. Не возвращать `IQueryable` в Application.

**Статус: устранён полностью.** Добавлены отдельные `IDashboardSnapshotReadQuery` и `IMonitoringRecipientReadQuery` в Application с Infrastructure-реализациями. Dashboard projection не содержит endpoint URL, visibility применяется до пользовательских DTO; recipient query пакетно materialize-ит users с ролью и permissions без per-user N+1. `ObjectVisibilityPolicy` осталась чистой синхронной политикой, `/api/objects` не возвращён.

## Рекомендуемый порядок исправлений

1. Исправить enum JSON/TypeScript-контракт и добавить реальный serialization test.
2. Реализовать UI управления пользователями/permissions и безопасный logout.
3. Запретить self/last-admin lockout.
4. Добавить optimistic concurrency и тесты гонок monitoring/admin.
5. Отвязать Telegram от monitoring cycle через durable outbox/dispatcher и согласовать реальную семантику доставки.
6. Ограничить monitoring HTTP transport.
7. При отдельном расширении объёма подготовить deployment/release-контур и выполнить end-to-end smoke-проверку.
8. Устранить snapshot storm, лишнее раскрытие endpoint и несогласованные Web error contracts.
9. Выполнить низкоприоритетный рефакторинг только после закрепления поведения тестами.
