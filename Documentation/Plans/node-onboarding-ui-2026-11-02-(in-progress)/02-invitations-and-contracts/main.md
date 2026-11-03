# 02. Приглашения и контракты

Статус: `completed` (2026-11-03). Зависимости: [01](../01-scenarios-and-decisions/main.md).

## Цель

Создать одноразовое приглашение, которое переживает перезапуск и смену лидера, выдаёт ограниченные полномочия и не создаёт два узла при конкуренции или retry.

## Работы

1. Ввести типизированные состояния приглашения и попытки, переходы reserve/consume/revoke/expire и OperationId/AttemptId. Подготовить закрытые DTO без private material. Конкретный API оформлять контроллерами с ServiceResult и единым envelope.
2. Получать автора из действующей принятой Administrator-авторизации. Для выдачи и отзыва использовать antiforgery; повторно проверять полномочия при принятии и критичных продолжениях. Изменение/отключение аккаунта не должно оставлять неподконтрольный допуск.
3. Реализовать криптографически случайный GUID v4, verifier, срок 15 минут и лимиты. Показывать полный код только администратору при выдаче, не передавать через URL/SignalR/логи. По истечении закрывать код; новая выдача кнопкой отзывает предыдущий неиспользованный код. Потеря ответа не создаёт второй активный код без явного действия; резервирование и уже начатый Join имеют отдельные полномочия продолжения.
4. Включить приглашения и результат резервирования в принятый pipeline, необходимый persistence/snapshot/schema/compatibility. Не строить второй журнал или глобальный UoW. Для локальной попытки создать отдельный сценарный контракт хранения.
5. Обеспечить атомарность конкурирующего использования, привязку к NodeId/storage/public key, exact request digest и устойчивый retry исходного запроса. Отзыв после принятого membership имеет отдельный результат, без ложного удаления voter.
6. Подготовить migrations только при реальном изменении схемы по профильному скиллу; применение к БД не входит в автоматическую разработку. Предусмотреть совместимый отказ старым узлам до изменения состояния.
7. Логировать issuance/revoke/reserve/reject/consume после соответствующего подтверждения, с безопасными invitation ID, attempt ID, node ID и actor ID. Проверять отсутствие повторного success при replay и отсутствие кода в сообщениях/исключениях.

## Затрагиваемые части

### Контракт, разработанный этапом 01

Реализовать [параметры](../technical-design.md#parameters) и состояния [E0–E3](../technical-design.md#enrollment): Issue/Replace/Revoke/Reserve/AuthorizeIssue/Bind/Admit, digest и accepted delegation автора, отдельные TTL 15/30/10 минут, ProcessEpoch и Pause/Continue после 24 часов inactivity. Совместно с Admit атомарно потребить invitation. Не ограничивать законный непрерывный catch-up 15 минутами; новый код не удаляет принятого voter. Exact replay выдаёт прежний результат, потерянный issuance response не восстанавливает plaintext. Сохранить invitation/attempt/delegation/revocation в accepted snapshot/strict codecs, local secret удалить после reserve.

Проверки/логи этапа: concurrent reserve/reissue, pending expiry, credential-revision автора, wrong digest, lost response/leader change/snapshot, границы всех TTL и секреты в exception paths — по [матрице](../technical-design.md#verification). Модели и порты этого этапа не зависят от будущих issuer/runtime adapters 03/04; doubles не дают production-допуска.

Определить также общие versioned **data contracts** GenesisAuthorization/ImportBaselineRef/LegacyHistory и SenderAssignment/SenderOutage/SummaryCut/coverage receipt, необходимые будущим 03а/03б/05а. В 02 это только форматы/строгие codec/snapshot границы; импорт, sender authority и доставка не реализуются. Так 03б может сохранить первоначальное pending sender assignment без зависимости от ещё не реализованного dispatcher 05а.

Domain/Cluster, Application/Cluster и независимые persistence/UoW-контракты, Infrastructure/Cluster и persistence, Web controllers/models. Актуализировать ближайшие AGENTS.md и технические разделы Shared cluster storage, Web application, Configuration и Testing в объёме реально введённых контрактов.

## Проверки и приёмка

- Разрешённая выдача, отказ без роли, CSRF, GUID v4, граница 15 минут, отзыв предыдущего кода при новой выдаче и неизменность payload при retry.
- Два разных узла одновременно предъявляют код: accepted допускается не более одного; первый может повторить исходную попытку.
- Потеря ответа после commit, перезапуск и смена лидера возвращают прежний результат.
- Snapshot/replay не оживляет использованный/отозванный код. Истечение приглашения не маскируется фиктивным успехом Pending.
- Истечение кода не отключает принятого участника; возобновление допускается только по действующим полномочиям исходной попытки.
- DTO, логи и исключения не раскрывают секрет; роль инициатора не берётся из JSON.
- Изолированные тесты не используют сеть/БД; provider-level атомарность включается в отдельно разрешённую приёмку этапа 07.

## Результат

Реализован полный подготовительный объём 02: типизированные состояния/переходы, immutable OperationId/AttemptId/request digest, accepted delegation, UUID v4/verifier/one-time response, атомарные reserve/replace/admit/cancel, отдельные сроки и ProcessEpoch/Pause/Continue. Приглашения и попытки включены в существующие reducer/projection/snapshot/replay и strict codecs; второй журнал и глобальный UoW не создавались. Common data contracts имеют versioned codec/snapshot validation, локальное хранение подготовлено портами с CAS и удалением protected code после reserve.

API выдачи/явной замены/отзыва/Continue/Cancel использует ServiceResult, accepted Administrator и password/lockout, antiforgery/rate limit/no-store; actor из JSON не принимается. Повторные авторизация и credential revision проверяются после await и при принятии. Потерянный ответ не восстанавливает plaintext, held Pending не отправляется автоматически. Structured audit следует за apply, replay известного результата не пишет повторный success; секреты не журналируются.

С разрешения пользователя сгенерирована `20261103075157_AddOnboardingInvitations` без строки подключения; metadata/model snapshot diff = 0. Миграция не применена. Compatibility v2 требует согласованного обновления всех voters, прежняя установка/defaults не менялись. IEnrollmentAuthority всегда false в production, настоящий enrollment/issuer/native Join/activation не реализованы. Старые membership/Activate не обходят новую попытку; issuer/local setup/runtime/import/Telegram dispatcher и экраны остаются запланированными.

Изолированные наборы прошли: **Domain 47, Application 266, Infrastructure Cluster 129, Web 143**, ошибок/пропусков нет. Проверки покрывают UUID/TTL, конкуренцию accepted-порядка, digest/binding, lost response/нового координатора без local Pending, actor change, process takeover, 25-часовой прогресс, held restore, cancellation/voter preservation, strict shared contracts/archives/snapshot и настоящий DotNext WAL reopen/replay; CSRF проверен настоящим MVC filter, API — прямыми вызовами. Отдельные production/test compile-check без warnings/errors. Статическая изоляция выполнена до запуска; provider-набор Infrastructure с БД исключён.

Provider-level SQL locking/atomicity, Up/Down, настоящие выборы/failover, fsync при аварии, сеть/TLS/Ubuntu/browser/полный Join не проверены и относятся к отдельной приёмке 07/следующим реализациям. Противоречия проектному протоколу не обнаружены: legacy 15-минутный Admit сохраняется только для прежних команд, schema-2 использует отдельные 10 минут. [Текущая техническая документация](../../../Technical%20documentation/Onboarding%20contracts.md), [команды/матрица проверок](../../../Technical%20documentation/Testing.md#приглашения-и-контракты-2026-11-03). Завершённый 01 и исторические планы не редактировались. Работа остановлена после 02.

## Подэтапы

Этап завершён. Ниже — декомпозиция уже зафиксированного результата, а не новые поручения или новые прогоны. Исходные требования, проверки и исторический результат выше сохранены; исправлены только пути ссылок.

| Подэтап | Зависимости | Статус |
| --- | --- | --- |
| [02.01. Состояния, IDs и строгие форматы](02.01-states-and-codecs.md) | [этап 01](../01-scenarios-and-decisions/main.md) | `completed` |
| [02.02. Выдача, замена и отзыв кода](02.02-issuance-and-api.md) | [02.01](02.01-states-and-codecs.md) | `completed` |
| [02.03. Конкурентный reserve и потребление](02.03-reservation-and-consumption.md) | [02.01](02.01-states-and-codecs.md), [02.02](02.02-issuance-and-api.md) | `completed` |
| [02.04. Полномочия автора и продолжение](02.04-delegation-and-resume.md) | [02.03](02.03-reservation-and-consumption.md) | `completed` |
| [02.05. Порты локального состояния](02.05-local-state-ports.md) | [02.01](02.01-states-and-codecs.md) | `completed` |
| [02.06. Форматы импорта и Telegram](02.06-future-data-formats.md) | [02.01](02.01-states-and-codecs.md) | `completed` |
| [02.07. Replay, безопасные события и фиксация результата](02.07-replay-and-safe-events.md) | [02.02](02.02-issuance-and-api.md), [02.03](02.03-reservation-and-consumption.md), [02.04](02.04-delegation-and-resume.md), [02.05](02.05-local-state-ports.md), [02.06](02.06-future-data-formats.md) | `completed` |
