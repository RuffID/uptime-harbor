# Этап 07. Telegram только от подтверждённого главного

Статус: completed

## Цель

Доставлять согласованные события из восстанавливаемой очереди одним главным, сохраняя per-chat результаты и допустимые редкие дубли в неопределённом окне.

## Зависимости и границы

После [05](../05-permissions-and-ownership/main.md) и [06](../06-monitoring-and-incidents/main.md). Использовать gate этапа 03 и журнал этапа 04. Реальные сообщения в Telegram отправляются только в отдельно разрешённой приёмке.

## Затрагиваемые части проекта

`Application/Cluster` (DeliveryQueue/Reducer/Projection/Eligibility/Message), `Infrastructure/Cluster` (AcceptedDeliveryDispatcher/DeliverySenderConsensus/ClusterTestDelivery), `Infrastructure/Telegram/TelegramHttpNotificationSender`, Web sender lifecycle/test-send/peer boundary, существующие сценарные UoW и snapshot/recovery; изолированные tests, актуальные Notifications/Monitoring/Testing и ближайшие AGENTS.md.

## Подэтапы

| Подэтап | Результат | Статус | Зависимости | Документ |
| --- | --- | --- | --- | --- |
| 07.1 | Очередь восстанавливается из принятой истории с устойчивыми per-chat delivery keys. | completed | 05, 06 | [План доставки и восстановление очереди](07.1-delivery-plan-and-recovery.md) |
| 07.2 | Первичная, повторная, recovery и тестовая отправки используют единый подтверждённый главный путь. | completed | 07.1 | [Gate для всех путей отправки](07.2-gate-and-all-send-paths.md) |
| 07.3 | Intent и результат реплицируются, retry сохраняет независимость чатов и связь восстановления. | completed | 07.2 | [Результаты, retry и partial/recovery](07.3-results-retries-and-recovery.md) |
| 07.4 | Удаления, reset и смена главного не запускают новые устаревшие попытки. | completed | 07.3 | [Отмены и гонки отправки](07.4-cancellation-and-failover-races.md) |

Работы распределены по документам подэтапов. Каждый подэтап включает обновление затронутых локальных `AGENTS.md` и фактической технической документации; проверки и статус фиксируются только по выполненному объёму. Сводный статус этапа учитывает все его подэтапы и общие критерии ниже.

## Границы ответственности подэтапов

07.1 хранит план и восстанавливает очередь, 07.2 подключает gate ко всем путям, 07.3 фиксирует результаты и retry, 07.4 закрывает отмены и гонки. До готовности всех путей и восстановления истории production sender не включается.

## Проверки

Изолированно с fake sender/time: потеря большинства до вызова, смена term после claim, process pause, известный успех при смене главного, partial delivery, полученный Telegram success без сохранённого результата, неоднозначный timeout, retry exhaustion, delete/cancel против отправки, фоновая доставка без открытой пользовательской сессии, test-send на ведомом.

Настоящие Telegram/chat permissions, SQL Server gate и физическая остановка главного между внешним успехом и локальной фиксацией — отдельная внешняя приёмка. Удаление дублей на стороне fake sender не доказывает exactly-once.

## Критерии приёмки

- [x] Никакой пользовательский или background-путь не обходит gate главного.
- [x] Подтверждённый результат не повторяется штатно; неопределённое окно описано и допускает повтор.
- [x] Согласованный intent переживает отказ главного; новый главный продолжает недоставленные сообщения.
- [x] Чужая видимость чата не влияет на доставку; ошибка одного чата не останавливает остальные.
- [x] Удаления и administrative reset сохраняют terminal cancellation и не вызывают recovery от старой конфигурации.

- [x] Чаты инцидентов узла берутся из принятого назначения этому NodeId; различающиеся endpoint-данные в сообщениях сохраняют узел-источник без искусственного общего uptime.

## Связанные вопросы


Клиентских блокирующих вопросов нет; ручной повтор после исчерпания попыток исключён из объёма.

Согласованное поведение закреплено в [требованиях](../business-requirements.md); клиентских блокирующих вопросов нет.

Детали readiness проверяются этапом 01; реальные bot/chat-настройки передаются локально в разрешённом вводе этапа 09.

## Результат выполнения

2026-10-29 завершены 07.1–07.4: восстановленная accepted очередь, настоящие renewal/attempt/result решения существующего журнала, единый sender gate всех путей, follower test-send через durable Pending, per-chat retry/exhaustion, поздний success и финальный recovery после unbind. Projection/snapshot/recovery хранят полную реальную историю attempts/results без синтетических подтверждений. Legacy Disabled-путь сохранён; новая schema migration не требуется.

Четыре production-проекта собраны отдельно последовательно без предупреждений/ошибок. Domain — 36, Application — 200, выбранные Infrastructure — 165, Web — 55: всего 456 успешных тестов без пропусков. [Команды, сценарии и ограничения](../../../Technical%20documentation/Testing.md#проверка-этапа-07). Минимальный порядок 10 использован внутри 07; этап 10 отдельно не начинался.

Общий план остаётся in-progress; 01–07 — completed, 08–10 — not-started, 09.2/09.3 — cancelled. UI, provisioning/fencing/controlled enable не реализованы; admission/trust/activation/delivery и общие gates не открывались, default Disabled сохранён. БД/SQL, включая SQLite, hosting, Docker, реальные peers/endpoints/Telegram, deployment и Git не запускались. SQL locking/atomicity, crash/fsync, exactly-once, browser acceptance и часовой догон не подтверждены. После 07.4 работа остановлена.
