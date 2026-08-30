# Результаты технического ревью первой версии

## Область проверки

Проверка выполнена относительно `Technical requirements.md` и текущей технической документации. В область проверки вошли Domain, Application, Infrastructure, Web, TypeScript-клиент, persistence, monitoring worker, Telegram-доставка, тесты и deployment-материалы.

Разрешённые Release-сборки и изолированные тесты выполнены после исправлений: Domain 15, Application 79, Infrastructure 34 и Web 31 тест прошли. Docker, runtime, PostgreSQL и проектные скрипты не запускались; выводы о runtime-развёртывании остаются основанными на статическом анализе и документации.

## Итог

P0-1 и P0-2 устранены и закреплены Web/Application-тестами. P0-3 исключён из объёма требований первой версии; остаются P1/P2-дефекты конкурентности, надёжности и безопасности, поэтому текущую версию ещё нельзя считать полностью готовой.

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
- `Documentation/Plans/Application implementation/03 - Authentication and visibility.md:5`, где этап уже отмечен завершённым.

Рекомендуемое решение: добавить AJAX-only административную страницу пользователей на TypeScript с созданием, блокировкой, удалением и атомарной заменой permissions. Контейнеры должны становиться доступными для выбора только вместе с родительским сервером. В layout добавить POST-выход. До реализации перевести этап 3 и общий план в статус `В работе`.

Обязательная проверка: browser/Web smoke-сценарий «создать пользователя → назначить сервер и один контейнер → войти этим пользователем → проверить скрытые объекты → заблокировать → убедиться в немедленной потере доступа → удалить».

### P0-3. Deployment/release-контур не завершён

**Статус исправления: исключено из объёма v1.** Требования `Technical requirements.md` не включают deployment orchestration; добавленные ранее `compose.yaml` и каталог `deploy` удалены. История замечания и рекомендации сохранены для возможного последующего этапа.

Этап 8 отмечен как `Приостановлен`: Docker Compose и orchestration намеренно исключены из первой версии. Отсутствие Compose не является дефектом ТЗ v1; этот абзац сохраняет исходное содержание замечания для истории ревью.

Кроме того, Web не вызывает `UseForwardedHeaders`. За TLS-terminating reverse proxy `UseHttpsRedirection` будет принимать решение по внутренней HTTP-схеме. Это создаёт риск циклических HTTPS-redirect и некорректного определения исходной схемы/host. `/health` содержит только пустой стандартный health check и не отражает готовность PostgreSQL.

Затронутые места:

- `Documentation/Plans/Application implementation/08 - Deployment and release.md:5`;
- `Documentation/Technical/Deployment.md:7`;
- `UptimeHarbor.Web/Program.cs:36`;
- `UptimeHarbor.Web/Program.cs:46`;
- `UptimeHarbor.Web/Pages/ServiceCollectionExtensions.cs:23`.

Рекомендуемое решение на момент ревью: завершить deployment/release-контур отдельным этапом. Эта рекомендация не является обязательной работой первой версии после уточнения границ ТЗ.

Обязательная проверка: чистое развёртывание, миграция пустой БД, вход и SignalR через Nginx, перезапуск/пересоздание Web и PostgreSQL-контейнеров без потери данных, контролируемая недоступность БД и восстановление.

## Высокий приоритет

### P1-1. Результат старой проверки может перезаписать административное изменение

`MonitoringService` повторно загружает объект и проверяет enabled/endpoint перед изменением, но между этой проверкой и `SaveChangesAsync` другой scope может выключить мониторинг или заменить endpoint. Для monitored entities не настроен concurrency token. В результате старая проверка способна записать `Available`, имя и времена уже выключенному объекту либо объекту с новым endpoint.

Keyed coordination защищает только две monitoring-проверки одного объекта и не координируется с административными запросами.

Затронутые места:

- `UptimeHarbor.Application/Monitoring/MonitoringService.cs:40-54`;
- `UptimeHarbor.Application/Monitoring/MonitoringService.cs:69-84`;
- `UptimeHarbor.Infrastructure/Persistence/Configurations/MonitoredObjectConfiguration.cs`, где отсутствует concurrency token.

Рекомендуемое решение: добавить оптимистическую конкурентность для серверов и контейнеров, предпочтительно через PostgreSQL `xmin`/явный version token. `MonitoringService` должен считать `DbUpdateConcurrencyException` ожидаемым устаревшим результатом, очистить tracking и не публиковать событие. Административный API должен возвращать `409 Conflict` при конфликтующем редактировании.

Обязательная проверка: provider-level тесты двух независимых DbContext для гонок «проверка vs disable», «проверка vs endpoint change» и «два административных update».

### P1-2. Единственный администратор может заблокировать систему

`UserManagementService.SetEnabledAsync` и `DeleteAsync` не запрещают текущему администратору отключить или удалить самого себя. Это особенно опасно, потому что UI/API не умеют создавать второго администратора. `AdministratorExistsAsync` считает отключённого администратора существующим, поэтому bootstrap после перезапуска не создаст доступную административную запись.

Затронутые места:

- `UptimeHarbor.Application/Users/UserManagementService.cs:84-129`;
- `UptimeHarbor.Infrastructure/Identity/IdentityUserAccountStore.cs:76-126`.

Рекомендуемое решение: запретить self-disable/self-delete по `accessContext.UserId`, а на уровне store/use case не разрешать отключение или удаление последнего включённого администратора. Если управление несколькими администраторами не входит в v1, административную запись вообще не следует показывать как блокируемую/удаляемую.

Обязательная проверка: тесты self-disable, self-delete, last-enabled-admin и bootstrap при наличии только отключённого администратора.

### P1-3. Telegram-доставка синхронно задерживает monitoring worker

После сохранения результата `MonitoringService` ожидает `MonitoringEventPublisher`. Telegram handler последовательно отправляет сообщения каждому чату, а timeout одного вызова равен 30 секундам. Пока отправки не завершены, задача объекта остаётся частью `Task.WhenAll` текущего цикла; следующий monitoring cycle не запускается. Несколько медленных чатов способны задержать проверки всех объектов на минуты.

Это противоречит требованиям о регулярных независимых проверках и о том, что ошибка одного чата не должна мешать мониторингу и другим чатам.

Затронутые места:

- `UptimeHarbor.Application/Monitoring/MonitoringEventPublisher.cs:8-16`;
- `UptimeHarbor.Infrastructure/Telegram/TelegramMonitoringEventHandler.cs:41-54`;
- `UptimeHarbor.Infrastructure/ServiceCollectionExtensions.cs:118`.

Рекомендуемое решение: после транзакции мониторинга записывать durable notification jobs/outbox и завершать проверку объекта. Отдельный background dispatcher должен читать задания, ограниченно параллелить получателей, применять retry/backoff и сохранять результат доставки. Простого `Task.Run` недостаточно: задания будут теряться при остановке процесса.

Обязательная проверка: зависший Telegram transport не задерживает начало следующего monitoring cycle; перезапуск между созданием задания и отправкой не теряет уведомление; ошибка одного чата не задерживает остальные сверх заданного bounded concurrency.

### P1-4. Гарантия «одно Telegram-сообщение» технически не выполняется

Текущий поток сначала вызывает Telegram, затем в другом scope сохраняет marker успешной доставки. При падении процесса или БД после `sendMessage`, но до `MarkDeliverySuccessfulAsync`, следующий цикл отправит то же сообщение повторно. Обратный порядок привёл бы к потере сообщения. Telegram Bot API не предоставляет idempotency key для `sendMessage`, поэтому строгий exactly-once контракт таким способом недостижим.

Затронутое место: `UptimeHarbor.Infrastructure/Telegram/TelegramMonitoringEventHandler.cs:45-54`.

Рекомендуемое решение: принять и явно задокументировать at-least-once семантику с редкими дубликатами либо изменить внешний transport/посредника на поддерживающий идемпотентность. Durable outbox существенно уменьшит окно сбоев и исключит потерю задания, но сам по себе не докажет exactly-once Telegram-доставку. Формулировку `Technical requirements.md` и тесты нужно привести к реально обеспечиваемой гарантии.

### P1-5. Monitoring HTTP adapter не ограничивает размер ответа и следует redirect по умолчанию

Ответ читается и разбирается потоково, но предельный размер JSON отсутствует. Скомпрометированный или ошибочно настроенный endpoint может вернуть большой/бесконечный ответ и создать высокий расход памяти/CPU сразу для множества параллельных проверок. Автоматические redirect также позволяют исходно разрешённому адресу перенаправить запрос на другой, в том числе внутренний, адрес.

Затронутое место: `UptimeHarbor.Infrastructure/Monitoring/HttpMonitoringEndpointChecker.cs`.

Рекомендуемое решение: ввести небольшой документированный maximum response size, проверять `Content-Length`, читать через ограничивающий stream и отклонять превышение отдельным failure kind. Настроить redirect policy явно; рекомендуемый вариант — отключить redirect и считать 3xx неуспехом. Для production ограничить egress сети Web-контейнера и определить допустимость private/link-local/loopback endpoint согласно реальной схеме эксплуатации.

Обязательная проверка: oversized body, chunked oversized body, redirect на другой host, redirect loop и cancellation во время чтения.

### P1-6. Тестовый набор не проверяет реальные Web-контракты и критические гонки

`JsonResultMapperTests` проверяет объект `JsonResult.Value`, но не прогоняет его через фактические MVC JSON options. Поэтому несовпадение enum-контракта осталось незамеченным. Большинство Web-проверок являются metadata/static tests; отсутствует smoke-проверка критических пользовательских потоков. Persistence tests проверяют metadata, но не конфликт двух реальных DbContext и не release-миграцию PostgreSQL.

Рекомендуемое решение:

1. добавить Web contract tests с реальной MVC-сериализацией для каждого DTO, используемого TypeScript;
2. добавить статическую проверку соответствия числовых enum и TypeScript-контрактов;
3. добавить provider-level concurrency tests;
4. вынести Docker/PostgreSQL/Nginx/browser проверки в отдельный release smoke suite, не смешивая их с изолированными unit tests;
5. не отмечать этап завершённым без прохождения соответствующего smoke-сценария.

## Средний приоритет

### P2-1. SignalR создаёт всплеск полных snapshot-запросов

Событие публикуется после каждой проверки каждого объекта, включая проверки без смены статуса. На каждое событие dashboard немедленно вызывает `loadSnapshot(false)`. При целевых 30 объектах один цикл способен создать около 30 полных HTTP snapshot-запросов на каждый открытый dashboard. Старые запросы не отменяются: `requestVersion` только запрещает им перерисовать UI.

Дополнительно `MonitoringEventRecipientResolver` для каждого события получает всех пользователей и отдельно загружает permissions каждого пользователя, что умножает N+1 запросы на число объектов.

Затронутые места:

- `UptimeHarbor.Application/Monitoring/MonitoringService.cs:55,85`;
- `UptimeHarbor.Web/wwwroot/ts/pages/dashboard.ts:101-113`;
- `UptimeHarbor.Application/Visibility/MonitoringEventRecipientResolver.cs:14-70`.

Рекомендуемое решение: debounce/coalesce snapshot reload на клиенте и отменять предыдущий fetch через `AbortController`. На сервере загружать пользователей и permissions пакетно; ещё лучше публиковать один обезличенный refresh-сигнал на пользователя по завершении набора изменений, сохраняя server-side visibility.

Обязательная проверка: 30 событий за короткий интервал приводят к одному или ограниченному числу snapshot-запросов.

### P2-2. Обычным пользователям раскрываются endpoint URL через неиспользуемое API

`/api/objects` доступен любому аутентифицированному пользователю и возвращает `EndpointUrl` в `AccessibleServerDto`/`AccessibleContainerDto`. Главная страница этот API не использует, а требования обычного пользователя предусматривают имя, описание, статус и времена, но не адрес внутренней проверки.

Затронутые места:

- `UptimeHarbor.Web/Controllers/ObjectsController.cs:10-36`;
- `UptimeHarbor.Application/Visibility/Models/AccessibleServerDto.cs:7`;
- `UptimeHarbor.Application/Visibility/Models/AccessibleContainerDto.cs:8`.

Рекомендуемое решение: удалить неиспользуемый controller/service, если для v1 нет потребителя. Если прямое чтение требуется, ввести отдельные read DTO без endpoint и других административных полей.

### P2-3. Сохранённое административное изменение может завершиться HTTP-ошибкой из-за SignalR

`ObjectAdministrationService` сначала сохраняет данные, затем ожидает `IObjectAdministrationChangePublisher`. Web-реализация не изолирует ошибку SignalR. При сбое push клиент получит ошибку операции, хотя данные уже сохранены, и повтор может создать дубликат или вызвать неожиданный конфликт.

Затронутые места:

- `UptimeHarbor.Application/ObjectAdministration/ObjectAdministrationService.cs`;
- `UptimeHarbor.Web/Hubs/SignalRObjectAdministrationChangePublisher.cs`.

Рекомендуемое решение: сделать refresh-сигнал best-effort и изолировать/логировать его ошибку после commit либо использовать outbox, если доставка такого сигнала должна быть надёжной. HTTP-результат не должен сообщать об откате уже зафиксированного изменения.

### P2-4. Единый JSON envelope не действует для ошибок model binding

Контроллеры помечены `[ApiController]`. Ошибки разбора JSON и автоматической валидации до входа в action будут возвращаться стандартным `ValidationProblemDetails`, а не документированным `{ success: false, message, field? }`. TypeScript helper предполагает собственный envelope и при другом теле выдаёт неуправляемую ошибку парсинга/общее сообщение.

Рекомендуемое решение: настроить `InvalidModelStateResponseFactory` на проектный envelope и сделать `sendJsonRequest` fail-fast, но предсказуемо обрабатывать не-JSON/пустые ответы. Закрепить malformed JSON, отсутствующее body и неверный тип поля контрактными тестами.

### P2-5. Защита Web-периметра неполна

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

### P3-2. Контракты репозиториев возвращают изменяемые `List<T>`

Application-интерфейсы спискового чтения возвращают `Task<List<T>>`, хотя потребителям не требуется изменять коллекцию и проектные правила требуют read-only materialized results.

Рекомендуемое решение: после более срочных исправлений заменить внешние контракты на `IReadOnlyList<T>` и оставить конкретный `List<T>` деталью Infrastructure/materialization. Не выполнять это одновременно с функциональными исправлениями конкурентности.

### P3-3. Visibility/read-query логика размножена

`AccessibleObjectsService`, `DashboardQuery` и `MonitoringEventRecipientResolver` отдельно загружают объекты/permissions и повторяют orchestration фильтрации. Это уже привело к лишнему `/api/objects` и N+1 запросам.

Рекомендуемое решение: оставить `ObjectVisibilityPolicy` чистым правилом, но добавить специализированные materialized query-контракты для dashboard и recipient resolution с пакетной загрузкой. Не возвращать `IQueryable` в Application.

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
