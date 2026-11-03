# 05а. Отправитель Telegram и сводки

Статус: `not-started`. Зависимости: [01](../01-scenarios-and-decisions/main.md), [02](../02-invitations-and-contracts/main.md), [03](../03-initial-setup/main.md), [04](../04-enrollment-and-trust/main.md). Готовый Join этапа 05 не требуется для реализации sender; совместная внешняя приёмка — в 07.

Граница после 03: setup/CanWork/consensus health/resign уже независимы от Telegram; BotToken не обязателен на несендере. Публичный BotId нового первого определяется локально, Join получает его через будущую authority. Managed delivery остаётся закрытым: sender assignment/renewal/dispatch и сводки этот результат не заменяет.

## Цель

Обеспечить одного назначенного отправителя общего бота независимо от Raft-лидера и после его простоя доставлять сводку вместо всех накопленных устаревших уведомлений.

## Работы

1. Ввести принятое назначение sender NodeId и версию/epoch его полномочий. Смена Raft-лидера не меняет отправителя. Существующие leader-bound authority key, gates, consensus-команды, reducer, ledger и result forwarding переработать согласованно: нельзя просто убрать IsLeader или разрешить внешнюю отправку без большинства.
2. Поддержать RenewSender/BeginDelivery/RecordDelivery от назначенного follower через текущий transport к лидеру с принятым подтверждением. Связать каждую попытку с sender identity, storage generation, версией назначения и актуальным правом. После утраты большинства/права/готовности новые внешние вызовы закрываются; старые результаты сохраняются с происхождением.
3. Оставить токен только в локальной конфигурации назначенного сервера. BotId, чаты, bindings и очередь общие; Join, snapshot и диагностика не копируют токен. Отсутствие токена на других voters не блокирует их работу. Предусмотреть явное первоначальное назначение администратора и проверку совпадения бота.
4. При недоступности отправителя продолжать доступную общую фиксацию событий, закрыть новые внешние отправки и явно показывать ожидание. Не выбирать запасного отправителя и не переносить токен. Восстановление требует catch-up и подтверждённой authority, а не только доступного HTTP.
5. Определить долговременные границы периода простоя и accepted-срез backlog по чату. Сформировать устойчивый план сводки: возникшие проблемы, устранённые за период и всё ещё актуальные. Для больших сводок использовать существующее разбиение по размеру; не добавлять в один чат события другого и не терять происхождение observer.
6. Атомарно принять план сводки и закрытие покрытых неначатых устаревших intents без фиктивного delivered. Исходные events/attempts сохраняются. События после среза не теряются и не закрываются прежней сводкой; retry/replay или смена лидера не создают новый независимый план для того же среза.
7. Сохранить правила удаления, disable/unbind, заслуженного recovery и неоднозначного исхода in-flight попыток. Неначатые устаревшие сообщения не рассылаются по одному; уже начатая попытка не объявляется отменённой задним числом. Telegram остаётся at-least-once: редкий дубль после неизвестного внешнего результата возможен.
8. Показать в UI отдельно назначенного отправителя, его доступность, локальную готовность токена, ожидание и состояние сводки. Роль follower не является ошибкой; HTTP 202/Committed ещё не означает доставку.
9. Логировать изменение назначения с доверенным actor ID, подтверждённое право, переход в ожидание/восстановление, принятый план и итог доставки с безопасными IDs/счётчиками. Не выводить token/URI Bot API/текст сообщений и не дублировать Information при poll/replay.

## Затрагиваемые части

### Контракт, разработанный этапом 01

Реализовать [независимую authority/устойчивый cut/ACK](../technical-design.md#telegram) с [параметрами](../technical-design.md#parameters). SenderAssignment отделён от coordinator term; follower Renew/Begin идёт текущим transport с exact authenticated origin и apply на sender. Изменить согласованно SenderAuthorityKey, NodeReadiness, SenderAuthorityGate, ClusterRuntimeState, lifecycle, runtime resign/Resign RPC/LeaderFailureWindow, DeliveryCommands/Reducer/Consensus/dispatcher/results. LeadershipToken follower не использовать как sender permit. Token validation/probe required только назначенному sender; лидер без токена остаётся исправным. Старые реальные authority variants/attempts не переписывать.

Outage coverage по чату, SealOutageCut C/frontiers и immutable state на C; PlanId/part IDs стабильны. План включает возникшие/устранённые/актуальные, unknown coverage честно отмечается. Plan + suppression неначатых intents атомарны; не возвращать отозванные summary sources в индивидуальную рассылку. Все части имеют настоящие attempts/results; ACK только после их terminal подтверждений, failed часть не продвигает coverage. Events после C идут в следующий cut, repeated outage продолжает известный plan. LegacyHistory из 03а/03б обрабатывается с её точностью, не synthetic delivery evidence.

Проверки и события перечислены в [матрице](../technical-design.md#verification): follower/leader без token, потеря majority/term/process, cutoff race/late raw facts, part failure/ACK, unbind/disable/delete, late success, crash после Telegram/plan/ACK. Дополнительно доказать: потеря sender оставляет два из трёх исправными для общих изменений, отсутствие token на несендерах не вызывает resign, до готового плана backlog не dispatch-ится по одному. Singleton используется для изолированной проверки authority, три узла нужны в приёмке 07, не как зависимость разработки.

Domain delivery commands/state, Application sender authority/readiness/queue, Infrastructure dispatcher/consensus/result storage/Telegram probe, Web workers/API/UI. Сохранить направление зависимостей и сценарные UoW. По реализации обновить ближайшие AGENTS.md, Notifications, Cluster runtime, Shared cluster storage, Web application, Configuration и Testing. Не менять завершённый план прежней leader-delivery.

## Проверки и приёмка

- Назначенный follower отправляет при готовом большинстве; лидер без назначения и peer без токена не отправляют. Смена лидера не запускает второго sender.
- В составе из трёх потеря отправителя оставляет мониторинг и общие изменения у двух узлов при прочих условиях готовности, Telegram ожидает. Возврат даёт catch-up, право и сводку, а не массовое проигрывание старой очереди.
- Связь отправитель–лидер обрывается до/после BeginDelivery и внешнего результата: нет отправки без права, потери подтверждённого результата и ложного exactly-once.
- В сводке различаются проблема, восстановление и актуальное состояние; несколько простоев, чаты, разбиение, новые события после среза, unbind/disable и повтор после crash не теряют данные.
- Изолированные тесты используют fake transport/time/sender без Telegram и БД. Настоящие сообщения, отказ/возврат follower и provider-level атомарность подтверждаются отдельно в этапе 07 после разрешения.

## Результат

Пока не выполнен. Новый контракт authority и сводок требует реализации и проверок; наличие старого единственного leader-sender не подтверждает этот сценарий.

## Подэтапы

Каждый файл ниже — отдельная задача. Общие требования выше сохраняются; файл подэтапа задаёт узкую границу исполнения и проверяемый результат.

| Подэтап | Зависимости | Статус |
| --- | --- | --- |
| [05a.01. Явное назначение единственного sender](05a.01-sender-assignment.md) | [этап 02](../02-invitations-and-contracts/main.md), [этап 03](../03-initial-setup/main.md), [этап 04](../04-enrollment-and-trust/main.md) | `not-started` |
| [05a.02. Authority follower и отделение от лидерства](05a.02-sender-authority.md) | [05a.01](05a.01-sender-assignment.md) | `not-started` |
| [05a.03. RenewSender и BeginDelivery через лидера](05a.03-renew-and-begin.md) | [05a.02](05a.02-sender-authority.md) | `not-started` |
| [05a.04. Capture результата и прежние attempts](05a.04-result-capture-and-legacy.md) | [05a.03](05a.03-renew-and-begin.md) | `not-started` |
| [05a.05. Период простоя и устойчивый cut](05a.05-outage-and-sealed-cut.md) | [05a.03](05a.03-renew-and-begin.md), [05a.04](05a.04-result-capture-and-legacy.md) | `not-started` |
| [05a.06. Стабильный состав и части сводки](05a.06-stable-summary-parts.md) | [05a.05](05a.05-outage-and-sealed-cut.md) | `not-started` |
| [05a.07. Атомарное принятие плана и suppression](05a.07-atomic-plan-and-suppression.md) | [05a.06](05a.06-stable-summary-parts.md) | `not-started` |
| [05a.08. Изменение bindings и replacement частей](05a.08-eligibility-and-replacement.md) | [05a.07](05a.07-atomic-plan-and-suppression.md) | `not-started` |
| [05a.09. Доставка частей и подтверждение coverage](05a.09-delivery-and-coverage-ack.md) | [05a.04](05a.04-result-capture-and-legacy.md), [05a.08](05a.08-eligibility-and-replacement.md) | `not-started` |
| [05a.10. Статусы sender и изолированная приёмка](05a.10-status-evidence-and-docs.md) | [05a.01](05a.01-sender-assignment.md), [05a.02](05a.02-sender-authority.md), [05a.03](05a.03-renew-and-begin.md), [05a.04](05a.04-result-capture-and-legacy.md), [05a.05](05a.05-outage-and-sealed-cut.md), [05a.06](05a.06-stable-summary-parts.md), [05a.07](05a.07-atomic-plan-and-suppression.md), [05a.08](05a.08-eligibility-and-replacement.md), [05a.09](05a.09-delivery-and-coverage-ack.md) | `not-started` |

Порядок определяется зависимостями в таблице. Внешние предпосылки и отдельные разрешения из общего плана сохраняются; переход к следующей задаче не происходит автоматически.
