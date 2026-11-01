# Web-приложение

## Эксплуатационные команды (09)

ClusterOperationsController GET/POST `/api/admin/cluster/lifecycle` требует accepted Administrator; POST — antiforgery, 202 — durable Pending. GET возвращает safe registry без public certificate/payload/hash/stamps. Internal lifecycle запрещён на browser/peer boundary. ClusterOperationsAdministration регистрируется в полном Web presentation graph, не в автономном Infrastructure module. Node Pending отображается отдельно от редакторов ресурсов, Cancel идёт через lifecycle API с сохранённым request/OperationId; held renewal выполняется явным typed API Replace с текущей OperationsRevision.

Диагностика дополнена term/membership/maintenance, OperationsRevision, conflicts/terminal delivery errors, возрастом проекции и последней подтверждённой синхронизацией команд. Эти поля не означают полного догона peers или срока возобновления Telegram. ClusterControlTool обрабатывает только явный `--cluster-control` до builder и не запускает host. [Форматы](Cluster%20control%20files.md).

## Интерфейс и API этапа 08

В enabled-режиме Index, Objects, Containers, Users и Notifications выбирают `_ClusterAdministration` и единственный `cluster.ts`. Общие таблицы отдельно показывают accepted configuration, durable local Pending, безопасные варианты конфликтов, availability/coverage, metadata каждого observer, диагностику и историю доставок. Typed Bootstrap modal поддерживает Server/Container/Chat/Binding/NodeChatRoute и Account; роли, владельцы и resource permissions отсутствуют. Disabled сохраняет прежние страницы без Users permissions UI. PageModel не загружает коллекции.

`GET /api/admin/cluster` читает whitelist принятого образа; `/pending?offset=N` возвращает страницы по 100 local records с приоритетом известного accepted result; `/operations/{id}` — известный безопасный результат, неизвестный результат даёт 404 и не означает отмену. `/diagnostics` доступен каждому активному принятому Administrator. POST `/commands` и `/pending/{id}/cancel` защищены antiforgery, используют ServiceResult/JsonResultMapper; LocalPending возвращает 202. Account-команды используют `/api/admin/users/commands`. Legacy dashboard/object/chat read/write в сетке возвращает 409 с указанием нового read API; accepted Users и прежний accepted test-send разрешены.

Редактор сохраняет ввод и отправленный request только в памяти вкладки. Retry использует те же OperationId/control ID и подготовленный payload; изменение отправленного запроса требует явной новой команды от перечитанной revision. Ошибка сети/потеря ответа/конфликт не очищает форму. Replace/Cancel показывают реальный результат, включая AlreadyDecided и отдельный replacement outcome. Held backup сверяется с известной историей; подтверждение создаёт Replace с текущей base revision, Cancel не освобождает исходный payload. Удаление требует явного глобального подтверждения и показывает известные зависимости. Пароль восстановленной/конфликтной заявки вводится заново.

`clusterApi` использует существующий HTTP helper; DOM строится DOM API. `clusterRefresh` отменяет и отбрасывает устаревшие чтения. Раз в 10 секунд, после reconnect и по сигналам с coalescing 200 мс заново загружаются защищённые accepted/Pending/diagnostics; refresh не пересоздаёт modal. 401/403 очищает защищённые списки и редактор, закрывает SignalR. Серверный accepted session/RecoveryVersion контроль продолжает отзывать доступ после apply/fault.

В браузер не сериализуются ConfigurationValue/RevisionCommand/ConflictVariant/RevisionImage, hash/stamps/credentials, raw JSON и сообщения Telegram. Account settings показывают только login/Enabled. Test-send использует устойчивый ID и принятую очередь: 202 и Committed команды не означают доставку. Terminal failure остаётся видимой без ручного retry/countdown. Фактического controlled enable установки не было; compilation и stubs не являются browser acceptance.


## Сборка sender-компонентов (07)

AddClusterWeb подключает DeliverySenderConsensus, AcceptedDeliveryDispatcher и ClusterDeliveryWorker к существующим RevisionDispatcher/IRevisionTransport. ClusterRaftHandle отделяет writer от runtime lifecycle и устраняет DI цикл с SenderAuthorityGate. Конструкторы/DI validation не запускают host, WAL или HTTP.

Peer revision boundary проверяет storage generation также для delivery payload. Enabled test-send передаётся через ClusterTestDelivery в durable Pending и возвращает общий JSON envelope с HTTP 202; ответ не подтверждает фактическую доставку. Disabled сохраняет прежний синхронный ответ. Авторизация повторяется перед отправкой; общий browser gate не открыт. UI очереди реализован в 08: Pending, принятие команды и доставка различаются. [Доставка](Notifications.md#принятая-доставка-этапа-07).

## Сборка monitoring компонентов (06)

AddClusterWeb регистрирует ObservationReplicationPump/MonitoringCoordinator рядом с RevisionDispatcher и IRevisionTransport; Infrastructure регистрирует cycle/peer capture, а scoped ObservationCaptureService — рядом с его persistence UoW. ClusterMonitoringWorker завершает работу без действий в Disabled; enabled требует действительных readiness/recovery/activation proof.

Peer batch до dispatch сверяет storage generation monitoring/delivery/lifecycle payload с аутентифицированным envelope и отвергает internal lifecycle. Пользовательская Administrator authorization отделена от системного source; browser routes не получили обход gate. Peer health возвращает только безопасный MonitoringProgress. Sender/attempt lifecycle подключён в 07; общий UI — в 08, controlled enable — в 09. Регрессия DI разрешает граф без старта host/WAL/HTTP; это не browser acceptance.

## Граница 05.1 и этапа 04

Рабочие ACL repository/UoW, SetVisibility use case/request и endpoint `/api/admin/users/{id}/visibility` удалены. Users permissions UI удалён в 08; compatibility DTO-поля остаются пустыми. 05.2–05.4 подключили accepted sign-in/cookie/SignalR и account API; 08 расширил его conflict/Replace/ConfirmRestored результатами. Browser gate закрыт; peer batch проверяет origin, accepted actor или внутреннюю provenance.

## Peer-периметр этапа 03

`ConfigureClusterListeners` проверяет отдельные именованные Kestrel `Browser`/`Peer` endpoints; Direct требует mTLS, внутренний HTTP разрешён только в режиме `NginxIsolatedHttp` с изоляцией/точным allowlist. `UseClusterPeerBoundary` находится до forwarded headers и cookie authentication: browser listener не обслуживает `/internal`, peer listener не обслуживает пользовательские endpoints. Enabled-режим до готовности будущих lifecycle/activation возвращает 503 на browser routes, включая login и account API: регистрация компонентов 05 не открывает gate. Peer health и native Raft доступны только после envelope/certificate/trust проверки; неизвестные будущие routes не открыты. Это не новое административное UI. Composition и ограничения — [Cluster runtime.md](Cluster%20runtime.md).

## Composition root

`UptimeHarbor.Web/Program.cs` выполняет только настройку host, модульные DI-вызовы, middleware и endpoint mapping. Прикладная логика в нём не размещается.

Текущий pipeline:

1. production exception handler и HSTS вне Development;
2. обработка `X-Forwarded-*` только от явно настроенной proxy-сети с `ForwardLimit = 1`;
3. routing;
4. security headers/CSP и login rate limiting;
5. authentication;
6. authorization;
7. static assets;
8. MVC controllers;
9. Razor Pages;
10. SignalR hub.

TLS завершается на Nginx. Web всегда принимает внутренний HTTP и не вызывает `UseHttpsRedirection`; внешний HTTP → HTTPS redirect и передача доверенного `X-Forwarded-Proto: https` принадлежат Nginx.

До построения pipeline `InitializeInfrastructureAsync` проверяет подключение к SQL Server, при наличии pending migrations создаёт backup и применяет их, после чего выполняет bootstrap ролей и начального администратора. Ошибка или 60-секундный timeout database startup останавливают запуск до построения HTTP pipeline.

## Типы endpoint

- Пользовательские страницы реализуются Razor Pages.
- HTTP API должно реализовываться MVC-контроллерами с attribute routing.
- Minimal API handlers `MapGet`, `MapPost`, `MapPut`, `MapDelete` и `MapPatch` не используются.
- SignalR hub сопоставлен по пути `/hubs/monitoring`.
- API snapshot главной панели сопоставлено под `/api/dashboard` и требует аутентификацию.
- API управления пользователями сопоставлено под `/api/admin/users` и требует роль `Administrator`.
- API управления серверами и контейнерами сопоставлено под `/api/admin/objects`, требует роль `Administrator`, а все изменяющие методы защищены antiforgery.
- API управления Telegram-чатами и привязками сопоставлено под `/api/admin/notifications`, требует роль `Administrator`, а все изменяющие методы и тестовая отправка защищены antiforgery.

`MapHub` является специализированным mapping платформы, а не обработчиком бизнес-API.

## Presentation DI

`AddWebPresentation` регистрирует controllers, Razor Pages и SignalR. Новые presentation-зависимости должны добавляться через этот или более узкий extension-метод, а не прямыми lifetime-регистрациями в `Program.cs`.

## Razor Pages

Страницы входа и выхода находятся в `/Account`. Для всего каталога Razor Pages действует обязательная авторизация; анонимный доступ разрешён только странице входа и странице ошибки.

Форма входа размещена в ограниченном Bootstrap `container` с колонками `col-12 col-sm-8 col-md-6 col-lg-4`: на телефоне занимает доступную ширину, на широком экране остаётся компактной, несмотря на общий fluid layout. POST, antiforgery и cookie-сценарий не изменены.

Изменяющие формы должны использовать POST, antiforgery и серверную авторизацию. API-контроллеры должны возвращать явные HTTP-контракты и также проверять роль/доступ.

В Disabled страница «Сервера» находится в `/Admin/Objects`, следующая ссылка «Контейнеры» в меню ведёт на `/Admin/Containers`. PageModel не загружает коллекции: Razor содержит только статические контейнеры и формы, а данные загружает TypeScript-модуль через MVC API. Каждая страница содержит собственную таблицу и отдельные modal создания/редактирования. В редакторе сервера вместо таблицы контейнеров находится multiSelect свободных и уже связанных контейнеров; настройки и полный выбор с версиями контейнеров сохраняются одним PUT. Создание и редактирование контейнера выполняются на Containers, сервер необязателен: radioSelect содержит вариант «Без сервера». Общие GET/POST контейнеров находятся под `/api/admin/objects/containers`, прежний вложенный маршрут сохраняется. После закрытия окна фокус возвращается в список или поиск; ошибки остаются внутри окна. Серверная форма передаёт обязательное уникальное имя сервера отдельно от последнего внутреннего имени, полученного при проверке. DTO содержит числовой `version`; update/delete возвращает его обратно как expected version. Stale request получает envelope `{ success: false, message }` и HTTP `409 Conflict`, после чего UI может перечитать актуальный список. Интерфейс работает без перезагрузки, создаёт динамические списки через DOM API без `innerHTML`, показывает серверные ошибки рядом с полями и требует подтверждение удаления. TypeScript собирается строгим `Microsoft.TypeScript.MSBuild` 7.0.1 из `wwwroot/ts` в generated-каталог `wwwroot/dist`, который не хранится в Git; target `PrepareTypeScriptForStaticWebAssets` выполняет новые package targets `SetTscExecutable` и `RunTscCompile` перед `ResolveProjectStaticWebAssets` и `ResolveCoreStaticWebAssets`. Поэтому актуальные outputs восстанавливаются до разрешения static assets. `CleanTypeScriptOutputs` в эту цепочку не входит, поскольку Static Web Assets фиксирует items до выполнения targets. MSBuild сохраняет target `ES2022` и не генерирует source maps, как и `tsconfig.json`. Удалённые в TypeScript 7 targets старой MSBuild-цепочки не используются.

В Disabled административная страница уведомлений находится в `/Admin/Notifications` и следует тому же AJAX-only контракту. Создание и редактирование чата, а также создание привязки выполняются в Bootstrap modal; основная область показывает таблицу чатов и server/container bindings с отдельной строкой на привязку, именем и ID чата. Чаты без привязок также отображаются. Поиск учитывает чат, его ID и объекты; действия чата и удаления привязки находятся в разных колонках. Кнопка «Отправить тест» отправляет реальное сообщение, что объясняется подсказкой заголовка. Bot token отсутствует в HTML, TypeScript и API payload. Состояния loading, empty, error и saving управляются клиентским модулем, опасные удаления требуют подтверждения.

В Disabled `/Admin/Users` создаёт пользователя и редактирует состояние аккаунта в Bootstrap modal через AJAX. Единственная роль — Administrator; permissions и загрузка серверов/контейнеров для них удалены. Успех обновляет список и закрывает modal; ошибка сохраняет окно, закрытие во время запроса блокируется. Фокус возвращается в список. В enabled действует общий cluster editor, описанный выше.

В Disabled главная страница `/` содержит статические контейнеры панели и не загружает коллекции в PageModel. TypeScript получает snapshot через `/api/dashboard`, показывает заданное имя сервера и отдельное внутреннее имя, выполняет server-side поиск по обоим именам и описанию. Серверы и контейнеры отображаются строками общей Bootstrap-таблицы; родитель контейнера указан в колонке «Сервер». Локальный фильтр статуса применяется к каждому объекту, кнопка «Обновить» перечитывает snapshot. Состояния loading, empty, no-results и error остаются отдельными. Пачка SignalR refresh-событий объединяется окном 200 мс. Каждый новый snapshot-fetch отменяет прежний через `AbortController`; штатный `AbortError` не отображается пользователю. Reconnect отменяет отложенный refresh и немедленно загружает актуальный snapshot. Таблица сохраняется при замене строк, поэтому SignalR refresh не пересоздаёт scroll-контейнер.

В Disabled запрос `/api/admin/users/{id}/enabled` отправляется только при изменении переключателя относительно local state. Self-disable/self-delete и последний активный Administrator защищены сервером. Формы назначения ресурсных прав нет.

### Общие таблицы и подсказки

В legacy dashboard серверный JSON содержит nullable `startedAtUtc` и `uptimeSeconds`. Колонки «Запущен» и «Текущий uptime» показывают их для серверов и контейнеров; дата выводится в UTC. Продолжительность считается от времени snapshot и последнего известного времени запуска, поэтому сама по себе не доказывает непрерывную доступность объекта.

Фильтры статуса на главной, «Серверах» и «Контейнерах» имеют одинаковую ширину `14rem` и не сжимаются в flex-панели, чтобы подписи кнопки и вариантов помещались в одну строку.

Тёмная Bootstrap-тема сохранена. Layout использует всю ширину окна с адаптивными отступами. Над основными таблицами поиск и фильтры находятся слева, компактные кнопки создания — справа. Поля поиска расширены через `size` и Bootstrap `w-auto mw-100`, чтобы текст подсказки помещался. Объекты фильтруются по имени, адресу, описанию и статусу; пользователи — по логину и состоянию учётной записи. Объекты и пользователи редактируются в модальных окнах. Таблиц ресурсных permissions нет; cluster refresh не пересоздаёт редактор.

`wwwroot/ts/shared/dataTable.ts` создаёт таблицы DOM API: семантические заголовки, закреплённая шапка, горизонтальный и вертикальный scroll. Максимальная высота контейнера рассчитывается по его положению и высоте окна с отступом 48 px и нижней границей 160 px; пересчитывается после render, при resize окна и изменении размеров body.

`tableColumnResizing.ts` и `helpTooltip.ts` перенесены из `AquaByteLedger.Web/wwwroot/ts/shared` и адаптированы. Стабильные ключи страницы, таблицы и колонки определяют localStorage-ключ с префиксом `uptimeHarborTableColumnWidth`. Ширина меняется перетаскиванием правого края заголовка или стрелками клавиатуры на resize-кнопке и применяется к заголовку и ячейкам. Подсказки у сложных заголовков и действий используют цвета текущей Bootstrap-темы, открываются при наведении/фокусе, закрепляются нажатием, закрываются по Escape или нажатию снаружи и следуют за trigger при scroll. Заголовки поясняют UTC-время, внутреннее имя, статус, uptime и Telegram-привязки.

Перед началом resize фиксируются фактические ширины всех колонок и таблицы, затем снимается `min-width: 100%`. Это исключает перераспределение ширины при первом нажатии. При движении применяется точная дельта указателя, после завершения сохраняются все ширины; отмена восстанавливает исходную ширину выбранной колонки.

`shared/switchAnimations.ts`, подключённый общим layout, анимирует положение и цвета переключателей с `data-animated-switch` через Web Animations API за 300 мс; reduced motion отключает анимацию. Новые CSS для этого не используются. Статус SignalR на главной называется «Данные обновляются автоматически»; потеря связи явно сообщает о недоступности автообновления и предлагает кнопку «Обновить».

Web и TypeScript собираются без ошибок; изолированные frontend-тесты проверяют геометрию resize. Проверка resize, sticky header, scroll, анимаций, подсказок и modal lifecycle в настоящем браузере остаётся открытой.

`searchableMultiSelect.ts` и `searchableRadioSelect.ts` скопированы из AquaByte-Ledger и адаптированы к тёмной Bootstrap-теме, optional radio-значению и состоянию disabled. MultiSelect использует checkbox, одиночные выборы уведомлений и фильтров используют radio. После изменения options/disabled вызывается controller.refresh(); при закрытии modal закрываются панели. При отсутствии вариантов поле отключено и показывает «Нет объектов.» (для чата — «Нет чатов.»). Окно добавления Telegram-привязки доступно и при пустых справочниках, но отправка отключена до выбора чата и объекта. Снятие saving сохраняет эти ограничения.

Snapshot панели дополнен `unassignedContainers`: контейнеры без сервера отображаются администратору с колонкой «Без сервера», обычным пользователям не раскрываются. Их независимые monitoring и Telegram bindings сохраняются.

Публичный `/api/objects` удалён как неиспользуемый v1 endpoint, раскрывавший endpoint URL. Пользовательские read-контракты dashboard не содержат endpoint и административные поля, кроме предназначенных для показа имени и внутреннего имени; `/api/admin/objects` сохраняет имя и endpoint URL для редактирования.

`JsonResultMapper` возвращает единый envelope и при field-specific validation добавляет необязательное поле `field`. Остальные ошибки сохраняют контракт `{ success: false, message: ... }` без дополнительного поля.

`InvalidModelStateResponseFactory` переводит автоматические `[ApiController]` ошибки в тот же envelope с HTTP 400. Ошибки body получают безопасное сообщение без `field`, ошибки конкретного поля — детерминированное camelCase-поле; exception message JSON parser и CLR-типы в ответ не включаются. TypeScript HTTP helper сохраняет нативный `AbortError`, возвращает `ApiError` с HTTP status для JSON, пустых и non-JSON error responses и по-прежнему fail-fast отклоняет неожиданный пустой успешный ответ.

Login Page имеет отдельную fixed-window policy: не более 10 попыток в минуту на `RemoteIpAddress`, без очереди. `X-Forwarded-For` влияет на этот адрес только после существующей проверки trusted proxy network. Ответ 429 одинаков для любого логина. Dashboard, admin API, SignalR и остальные страницы login-specific policy не получают.

Общий layout показывает логин аутентифицированного пользователя непосредственно перед POST-кнопкой выхода.

В `wwwroot/lib` поставляется только Bootstrap: общий layout подключает `bootstrap.min.css` и `bootstrap.bundle.min.js` для оформления, адаптивного меню и модальных окон. Неиспользуемые jQuery, jQuery Validation и jQuery Validation Unobtrusive удалены вместе с их подключениями и неиспользуемым `_ValidationScriptsPartial.cshtml`. Страница входа сохраняет серверную валидацию; клиентские модули работают через TypeScript и DOM API.

На все ответы добавляются `X-Content-Type-Options: nosniff`, `Referrer-Policy: no-referrer`, `X-Frame-Options: DENY` и применяемая политика `Content-Security-Policy`: `default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self'; font-src 'self'; connect-src 'self'; object-src 'none'; base-uri 'self'; frame-ancestors 'none'; form-action 'self';`. Политика разрешает только локальные scripts/styles/images/fonts/forms и same-origin SignalR transport, запрещает object/embed и framing, не содержит `unsafe-inline`, `unsafe-eval`, wildcard или CDN origins.

Главная страница подключает локальный `UptimeHarbor.Web/wwwroot/vendor/signalr/signalr.min.js` с `asp-append-version="true"`. Asset предоставлен локально по `D:\Media\User\Downloads\signalr.min.js`, содержит версию `10.0.11`, скопирован без изменения; SHA-256: `97E9B97E642A72E5A470917147A2BF79F86CAD829A5C6786ADB10614D248BB95`. Дополнительный license/notice-файл вместе с предоставленным asset не поставлялся.

## SignalR

`MonitoringHub` требует аутентификацию и не содержит вызываемых клиентом методов выбора пользователя или группы. Стандартный SignalR user-id берётся из аутентифицированного `NameIdentifier`. `SignalRMonitoringEventHandler` получает разрешённые user-id через Application resolver и не делает broadcast данных объекта.

Событие `MonitoringObjectChanged` содержит только `objectId` и `objectKind`; получатель перечитывает защищённый snapshot. Клиент включает автоматическое переподключение, повторяет initial start после исчерпания reconnect-попыток и после `onreconnected` полностью заменяет состояние актуальным snapshot.

После успешного административного сохранения `SignalRObjectAdministrationChangePublisher` отправляет всем аутентифицированным открытым панелям только событие `ObjectAdministrationChanged` без данных объекта. Получатель должен перечитать разрешённый ему snapshot через защищённый API; событие не является источником истины и не раскрывает скрытые объекты.

Административный SignalR refresh является best-effort: publisher вызывается только после commit, изолирует и журналирует transport exception без данных объекта. Ошибка push не меняет успешный `ServiceResult` сохранённой операции и не требует повторного HTTP-запроса; после save/concurrency error publisher не вызывается.

## Минимальный контракт общих пользователей (05.2–05.4)

В enabled-регистрации `/api/admin/users` читает принятые аккаунты с Revision, а `/api/admin/users/commands` принимает OperationId, UserId, BaseRevision, Action (Create/Update/Password/SetEnabled/Delete) и необходимые Login/Password/IsEnabled. ActorUserId и NodeId назначаются сервером; выбора роли и ресурсных прав нет. Изменения защищены Administrator metadata, antiforgery и повторной принятой авторизацией Application/reducer. HTTP 202 означает только durable Pending, а не принятый успех. `/api/admin/users/operations/{operationId}` возвращает принятый безопасный итог; неизвестный итог — NotFound, не ложный успех. Старые mutating routes в сетке возвращают конфликт и не пишут локальный Identity store.

Cookie validator проверяет accepted credential binding на каждом запросе. MonitoringHub принимает только валидную локальную сессию; apply/fault отзывает подписки через MonitoringConnections. Publishers адресуют только проверенные connection IDs. Hash/stamps/credentials и raw конфликтные payloads в DTO/SignalR не входят. Enabled UI использует общий cluster editor с Pending и безопасными типизированными вариантами конфликтов; SignalR передаёт только invalidation. Browser gate остаётся закрытым до подтверждения эксплуатационных зависимостей этапа 09.
