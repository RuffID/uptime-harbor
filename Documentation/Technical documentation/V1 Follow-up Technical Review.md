# Повторное техническое ревью UptimeHarbor v1

> Последующий статус на 31 августа 2026 года: описанные ниже P1 recovery cancellation и P2 Telegram unique conflict исправлены в коде и разрешённых изолированных тестах. Persistence после ревью переведён на Microsoft SQL Server с baseline `20260831015604_InitialSqlServerSchema`; упоминания PostgreSQL и прежней migration ниже сохранены только как исторический контекст момента ревью и не описывают текущий provider. SQL Server runtime, применение baseline, реальные 2601/2627 и row-lock ordering не проверялись.

## Итоговая оценка готовности

Версия v1 не готова к подтверждённому runtime-релизу. В исходном коде закрыты прежние P0-1, P0-2, P1-2—P1-5, P2-2—P2-3 и P3-1—P3-3; P0-3 исключён из границ v1. Однако остаётся новая P1-ошибка семантики recovery при отключении объекта, а также P2-ошибка конкурентного создания Telegram-данных. Критические runtime-проверки PostgreSQL, настоящего MVC input formatter, браузера/SignalR, reverse proxy и перезапуска процесса не выполнялись. Поэтому прохождение изолированных тестов не является доказательством готовности.

Текущий счётчик требующих действий: **P0 — 0, P1 — 3, P2 — 4, P3 — 0**.

## Выполненные проверки

- `dotnet build .\UptimeHarbor.Domain\UptimeHarbor.Domain.csproj --configuration Release` — успешно.
- `dotnet build .\UptimeHarbor.Application\UptimeHarbor.Application.csproj --configuration Release` — успешно.
- `dotnet build .\UptimeHarbor.Infrastructure\UptimeHarbor.Infrastructure.csproj --configuration Release` — успешно.
- `dotnet build .\UptimeHarbor.Web\UptimeHarbor.Web.csproj --configuration Release` — успешно.
- `dotnet test --project .\tests\UptimeHarbor.Domain.Tests\UptimeHarbor.Domain.Tests.csproj` — 16/16.
- `dotnet test --project .\tests\UptimeHarbor.Application.Tests\UptimeHarbor.Application.Tests.csproj` — 91/91.
- `dotnet test --project .\tests\UptimeHarbor.Infrastructure.Tests\UptimeHarbor.Infrastructure.Tests.csproj` — 61/61.
- `dotnet test --project .\tests\UptimeHarbor.Web.Tests\UptimeHarbor.Web.Tests.csproj` — 53/53.
- `docker compose -f .\compose.yaml config` — неприменимо: файл `compose.yaml` отсутствует.
- `docker compose -f .\compose.yaml build` — неприменимо по той же причине; команда завершилась с ошибкой открытия файла.

Web-хост, браузер, PostgreSQL, миграции, SQL, `docker compose up`, deployment и проектные скрипты не запускались.

## Повторная проверка прежних замечаний

| Пункт | Статус | Фактическая проверка |
|---|---|---|
| P0-1 enum MonitoringStatus | Устранён | `AdminServerDto`/`AdminContainerDto` используют числовой enum; TypeScript также использует 0/1/2; `AdminObjectJsonContractTests` сериализует через MVC `JsonOptions`. |
| P0-2 UI пользователей, permissions и logout | Устранён частично | Страница `/Admin/Users`, API, POST logout и серверные guards присутствуют. Проверены metadata/static и application tests, но браузерный сценарий создания, блокировки, permissions и logout не запускался. |
| P0-3 deployment/release | Исключён из v1 | Требования v1 не включают Compose/Nginx/orchestration; план этапа 8 помечен «Приостановлен». Фактически `compose.yaml` и Nginx отсутствуют. |
| P1-1 stale monitoring result | Устранён частично | `Version` — concurrency token, stale write преобразуется в `PersistenceConcurrencyException`, provider-тесты SQLite проходят. Migration не применялась к PostgreSQL, поэтому PostgreSQL runtime-поведение не подтверждено. |
| P1-2 последний администратор | Устранён | Self-disable/self-delete и last-enabled-admin guards есть в Application и Identity store; транзакционная проверка и concurrency stamp роли сохраняют инвариант. Нет конкурентного PostgreSQL-теста двух операций над администраторами; это осталось runtime-проверкой, а не неподтверждённым изменением кода. |
| P1-3 Telegram блокирует monitoring cycle | Устранён | Durable outbox и отдельный worker; jobs и recipients обрабатываются отдельно и параллельно. Изолированные тесты подтверждают независимость цикла и зависшего получателя. Реальный process restart/Telegram API не проверялся. |
| P1-4 exactly-once Telegram | Устранён | Требование и документация согласованы с at-least-once; окно дубликата после Telegram send до marker явно описано. |
| P1-5 размер ответа и redirects | Устранён | Лимит 64 KiB, проверка `Content-Length`, bounded read и `AllowAutoRedirect=false`; fake HTTP tests проходят. |
| P1-6 тестовое покрытие критических runtime-контрактов | Остаётся актуальным | Большинство проверок статические или SQLite/fake. Нет настоящего formatter pipeline, PostgreSQL migration/concurrency, browser burst/AbortController/CSP/SignalR и process-restart smoke. |
| P2-1 SignalR snapshot storm | Устранён частично | В TypeScript есть debounce 200 мс и `AbortController`; доказательства ограниченного числа запросов существуют только в static test, browser burst не запускался. |
| P2-2 endpoint URL обычного пользователя | Устранён | `/api/objects` удалён; dashboard/read DTO и projection не содержат URL; admin DTO сохраняют URL. Есть static security tests. |
| P2-3 ошибка после commit из-за SignalR | Устранён | Web publisher best-effort изолирует transport exception; application test подтверждает успешный commit без повторной операции. |
| P2-4 JSON envelope model binding | Устранён частично | `InvalidModelStateResponseFactory` и mapper возвращают `{success:false,message[,field]}`. Тесты вызывают factory напрямую; реальный `[ApiController]` input formatter для malformed JSON/missing body не запускался. |
| P2-5 Web perimeter | Устранён частично | Lockout, login rate limit, headers, CSP, forwarded headers и local SignalR asset настроены и покрыты static/unit tests. Не проверены за Nginx HTTPS/WebSocket/cookie runtime и фактическое поведение CSP в браузере. |
| P3-1 дублирование administrator policy | Устранён | Все три административных use case используют `IAdministratorAccessPolicy`; application tests проходят. |
| P3-2 изменяемые List в Application contracts | Устранён | Списковые контракты возвращают `IReadOnlyList<T>`, EF materialization остаётся в Infrastructure. |
| P3-3 дублирование visibility/read-query | Устранён | Dashboard и recipient queries разделены, materialized и пакетны; `/api/objects` не возвращён. |

## Оставшиеся проблемы

### P1-1. Recovery может быть отправлен после отключения мониторинга

**Тип:** функциональный дефект, надёжность и конкурентность.

**Нарушаемое требование:** отключение мониторинга переводит объект в `Unknown`, завершает текущий инцидент и не отправляет recovery-уведомление.

**Файлы и строки:** `UptimeHarbor.Application/Monitoring/MonitoringService.cs:54-66,93-105`; `UptimeHarbor.Domain/Monitoring/MonitoredObject.cs:84-97`; `UptimeHarbor.Infrastructure/Telegram/NotificationOutboxStore.cs:19-39`; `UptimeHarbor.Application/Notifications/NotificationDeliveryService.cs:56-72`.

**Сценарий:** объект находится в `Unavailable`, outage для incident A уже доставлен. Успешная проверка сохраняет `Available` и recovery job A в outbox. До обработки job администратор выключает мониторинг; `SetMonitoringEnabled(false)` очищает `CurrentIncidentId`. Dispatcher всё равно читает payload recovery A, видит marker outage A у binding и отправляет recovery.

**Последствия:** пользователь получает ложное «Доступен» после ручного отключения; состояние объекта и уведомление расходятся.

**Варианты решения:** добавить отмену recovery jobs текущего инцидента в транзакцию disable/endpoint reset; добавить явный `CanceledAtUtc`/тип отмены в outbox; перед доставкой recovery проверять актуальность состояния и отдельный признак отмены.

**Рекомендуемое решение:** в одной транзакции с отключением помечать незавершённые recovery jobs данного объекта/incident как отменённые, а dispatcher считать отменённую job завершённой без отправки. Не удалять job физически, чтобы сохранить диагностику.

**Закрепляющая проверка:** provider-level test: outage delivered → recovery job queued → disable → dispatcher не вызывает sender и job получает terminal cancelled state; отдельно проверить, что обычный recovery без disable доставляется.

### P2-1. Гонки уникальности Telegram-чата и привязки возвращают 500

**Тип:** функциональный дефект и конкурентность; одновременно нарушается единый JSON API-контракт.

**Нарушаемое требование:** административные сценарии должны возвращать ожидаемый конфликт через `ServiceResult`/HTTP 409, а API — единый JSON envelope, не необработанное исключение.

**Файлы и строки:** `UptimeHarbor.Application/Notifications/NotificationAdministrationService.cs:67-81,102-115,210-259`; уникальные индексы `UptimeHarbor.Infrastructure/Persistence/Configurations/TelegramChatConfiguration.cs:18` и `NotificationBindingConfiguration.cs:31-36`.

**Сценарий:** два администратора одновременно создают один Telegram `chatId` (или одну пару chat/object). Оба проходят предварительный `GetBy...` и видят отсутствие записи. Один `SaveChangesAsync` получает уникальный индекс; исключение `DbUpdateException` не преобразуется в `ServiceResult.Conflict` и доходит до Web как 500.

**Последствия:** UI получает HTML/непредусмотренный error вместо `{success:false,message}`, показывает неясную ошибку; конкурентная операция не имеет безопасного повторяемого результата.

**Варианты решения:** перехватывать только ожидаемое нарушение уникального ограничения и возвращать `Conflict`; использовать database-native insert/upsert с однозначным результатом; добавить сценарный concurrency abstraction.

**Рекомендуемое решение:** в Infrastructure преобразовывать нарушение конкретных unique constraints в независимое `PersistenceConflictException`, а Application маппить его в `ServiceResult.Conflict` с нужным `field`/сообщением. Не перехватывать прочие `DbUpdateException`.

**Закрепляющая проверка:** два независимых SQLite/PostgreSQL provider context для CreateChat/CreateBinding; один запрос должен вернуть 200, второй — HTTP 409 и JSON envelope без утечки SQL-текста.

## Новые найденные проблемы

Новые проблемы, подтверждённые текущим кодом, перечислены выше: recovery после disable (P1) и необработанные уникальные конфликты Telegram (P2). Других подтверждённых production-дефектов в проверенных слоях не установлено; оставшиеся пункты ниже относятся к отсутствию runtime-доказательств.

## Расхождения кода и документации

- `Documentation/Technical documentation/Notifications.md:13,23-25` описывает per-binding markers, но не описывает отмену уже поставленной recovery job при disable; фактический код такой отмены не имеет.
- `Documentation/Technical documentation/Testing.md` перечисляет malformed-body, browser lifecycle и SQLite/provider проверки, но формулировки не всегда отделяют прямой вызов factory/static source checks от реального MVC/browser runtime. Это создаёт завышенное впечатление доказанности.
- `Documentation/Technical documentation/Deployment.md:5,21-25` корректно фиксирует отсутствие Compose/Nginx как границу v1, но `docker compose` проверки неприменимы, потому что файл отсутствует; это должно оставаться явно непроверенным deployment-контуром.
- Планы этапов 02–07 используют статус «В работе», что соответствует невыполненным runtime-проверкам; общий план также «В работе». Этап 08 «Приостановлен» согласован с требованиями v1.

## Непроверенные runtime-риски

- PostgreSQL: применение миграции `20260830090000_AddMonitoringConcurrencyAndNotificationOutbox`, реальный `DbUpdateConcurrencyException`, типы `jsonb/timestamp with time zone`, каскады и восстановление после перезапуска.
- Реальное восстановление процесса между Telegram `sendMessage` и marker, lease после перезапуска, сетевые ошибки/429 Telegram и отсутствие утечки токена в production logging pipeline.
- Браузер: 30 быстрых SignalR-событий, фактическое число snapshot-запросов, отмена fetch, reconnect и работа CSP/WebSocket.
- ASP.NET Core runtime: malformed JSON, отсутствующее тело, неверные типы и formatter pipeline для каждого `[ApiController]`; проверены только factory/serialization helpers.
- Reverse proxy: trusted `X-Forwarded-*`, отсутствие внутреннего HTTPS redirect, внешний redirect на Nginx, `Secure` cookies и host/scheme при реальной Nginx-схеме. Nginx-конфигурации в репозитории нет.
- Разделение liveness/readiness и PostgreSQL probe под реальным окружением; `/health/ready` проверен только по регистрации кода.
- Сохранность PostgreSQL и Data Protection keys после фактического пересоздания контейнеров; Compose отсутствует.
- Полный browser smoke пользовательских сценариев: создание/блокировка/permissions пользователя, администрирование объектов и Telegram, POST logout.

## Рекомендуемый порядок исправлений

1. Исправить отмену recovery outbox при disable/endpoint reset и закрепить provider-level race test.
2. Обработать уникальные конфликты chat/binding как ожидаемый `409 Conflict` с единым JSON envelope и добавить concurrency tests.
3. Применить и проверить последнюю PostgreSQL migration в отдельной release-среде; проверить restart, backup/restore и Data Protection keys.
4. Выполнить реальный MVC malformed-input и browser smoke (SignalR burst/cancellation/CSP, admin UI и logout) без подмены статическими тестами.
5. Только после этого пересмотреть статусы этапов 02–07 и вынести подтверждённые ограничения deployment в release checklist.
