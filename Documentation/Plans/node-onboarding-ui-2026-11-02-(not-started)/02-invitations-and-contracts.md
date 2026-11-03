# 02. Приглашения и контракты

Статус: `not-started`. Зависимости: [01](01-scenarios-and-decisions.md).

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

Реализовать [параметры](technical-design.md#parameters) и состояния [E0–E3](technical-design.md#enrollment): Issue/Replace/Revoke/Reserve/AuthorizeIssue/Bind/Admit, digest и accepted delegation автора, отдельные TTL 15/30/10 минут, ProcessEpoch и Pause/Continue после 24 часов inactivity. Совместно с Admit атомарно потребить invitation. Не ограничивать законный непрерывный catch-up 15 минутами; новый код не удаляет принятого voter. Exact replay выдаёт прежний результат, потерянный issuance response не восстанавливает plaintext. Сохранить invitation/attempt/delegation/revocation в accepted snapshot/strict codecs, local secret удалить после reserve.

Проверки/логи этапа: concurrent reserve/reissue, pending expiry, credential-revision автора, wrong digest, lost response/leader change/snapshot, границы всех TTL и секреты в exception paths — по [матрице](technical-design.md#verification). Модели и порты этого этапа не зависят от будущих issuer/runtime adapters 03/04; doubles не дают production-допуска.

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

Пока не выполнен. Создание моделей и API не означает готовность присоединять узел: setup, доверие и lifecycle завершаются следующими этапами.
