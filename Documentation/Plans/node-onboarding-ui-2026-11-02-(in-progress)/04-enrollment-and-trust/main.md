# 04. Установление доверия

Статус: `in-progress`. После паузы 2026-11-03 работа возобновлена по поручению пользователя 2026-11-15: 04.01–04.07 завершены, следующий шаг — 04.08. Этап целиком не завершён. Зависимости: [01](../01-scenarios-and-decisions/main.md)–[03](../03-initial-setup/main.md).

Исходная граница после 03: закрытые production authority-порты и E0 без настоящего Bootstrap/Join. В 04 подключены production адаптеры, genesis/issuer/grants/activation нового первого, accepted ProcessEpoch, E1–E4 и двусторонние probes; сохранены local step CAS, исходные IDs/digests и E0 key. Это ещё не полный объём этапа. [Фактический setup](../../../Technical%20documentation/Local%20installation%20setup.md), [точка остановки](#результат).

## Цель

Превратить принятое приглашение в узкий проверяемый допуск конкретной новой установки, сохранив защиту peer-связи и восстановления.

## Работы

1. Реализовать согласованный enrollment-протокол на выделенных endpoints с явными capabilities, версиями, размером и timeout. Не размещать преддоверенный handshake в закрытом для него existing peer RPC и не ослаблять peer authentication ради доступа к форме.
2. Проверять адрес первого сервера и собственные advertised endpoints по правилам technical-design.md. Учитывать разрешённые частные сети, двустороннюю связность, DNS rebinding, redirect и запрет утечки кода на другой origin. Доступность HTTP UI не подменяет успешную peer-проверку.
3. Создавать private key локально, передавать только CSR/public material. Привязать принятую reservation к NodeId/storage/address/key. Реализовать встроенный ограниченный issuer по дизайну этапа 01: автоматическую подготовку первого узла, выдачу/установку сертификата и trust anchor, обновление/отзыв и проверку CA/SAN/pin/expiry. Внешний сервис, ручная выдача сертификатов и перенос ключей пользователем не требуются.
4. Реализовать process-bound online admission и отдельные узкие полномочия активации по дизайну этапа 01. Не синтезировать ручное подтверждение изоляции, не подписывать произвольный control из Web и не устанавливать operator-private/root CA key на узлы. Встроенный issuer не получает emergency/backup/maintenance-полномочий.
5. Проверять свежесть принятого приглашения и актуальную авторизацию инициатора перед выдачей полномочий. Ограничить количество параллельных попыток и срок reservation; обеспечить resume без повторной выдачи identity.
6. Сохранить ручные Restart/rejoin/backup/emergency/maintenance процедуры. Использованное приглашение не становится постоянным ключом восстановления или разрешением присоединить удалённый NodeId.
7. Не передавать Telegram-токен в enrollment, общих командах, snapshot или UI DTO. Его отсутствие на новом узле допустимо: токен остаётся только у назначенного отправителя. Готовность доставки и её authority реализуются этапом 05а отдельно от допуска нового узла.
8. Логировать принятый enrollment, отказ/expiry/revoke и безопасную идентичность сертификата; исключить private keys, invitation plaintext, токены и тела защищённых команд. Успех выдачи полномочия отделять от принятого членства и готовности.

## Затрагиваемые части

### Контракт, разработанный этапом 01

Реализовать [протокол issuer/E0–E8/grants/validators](../technical-design.md#enrollment). Новый dedicated pathLen=0 application CA создаётся первым автоматически, закрытый ключ остаётся только issuer node; существующий root/offline operator key не импортировать. GenesisAuthorization либо accepted RegisterEnrollmentIssuer разрывают цикл начального доверия. Browser HTTPS authenticates origin, CSR PoP/accepted template/Bind fingerprint дают конкретный leaf. Durable IssueId возвращает те же DER; CA signature сама не предоставляет authority.

Tagged proof заменяет общий FencingConfirmed только в новых online ветках, включая ClusterAccountBootstrapper; offline signature/manual isolation/CRL/maintenance сохраняются. DotNextConsensusAdapter получает exact genesis writer permit без раннего общего CanWork и произвольных команд. ManagedRestart разрешает карантинный запуск ранее целого хранилища до большинства, затем новый barrier/Enable; restore/clone/removed identity остаются offline. Exact changes перечислены по классам; server name/pin/KU/EKU, application revocation на каждый RPC, renew/retire/issuer replacement и process-epoch replay tests обязательны. Сроки — [parameters](../technical-design.md#parameters), события/негативные случаи — [verification](../technical-design.md#verification).

При реализации актуализировать устаревшую пометку Identity/AGENTS.md о будущем ручном lifecycle; не менять исторические планы. Интеграционный результат 03+04 — новый первый готовый кластер, но snapshot/membership orchestration второго реализуется 05.

Application enrollment/trust contracts, Infrastructure crypto/control/HTTP adapters и безопасное локальное хранение, Web setup/enrollment controllers и perimeter. Актуализировать местные AGENTS.md, Cluster runtime, Cluster control files, Configuration, Web application и Testing по фактическим изменениям.

## Проверки и приёмка

- Внешний сервер не получает полномочий без допустимой установки, приглашения и привязанного ключа.
- Нормальный путь сам подготавливает сертификаты без внешнего issuer и ручного копирования файлов; новый peer не требует Telegram-токена.
- Неверные TLS/CA/SAN/pin, чужой ClusterId, устаревший challenge, иной request digest и отозванный автор дают отказ.
- Изменение адреса через redirect/DNS не выводит запрос за разрешённые endpoints и не раскрывает код.
- Online grant не открывает emergency recovery, maintenance, clone или обычный Restart; прежние offline проверки продолжают действовать.
- Interrupted enrollment возобновляется той же попыткой; orphan credentials не активируют voter, автоматически не продлеваются и доступны для контролируемого отзыва.
- Fake crypto/HTTP/probes тестируют протокол изолированно. Настоящие TLS/issuer/Nginx и права файлов подтверждаются только этапом 07.

## Результат

### Точка остановки 2026-11-03

Работа приостановлена по прямой просьбе пользователя. Код сохранён в текущем состоянии; этап не принят и не помечен completed. Новые изменения кода и проверки при фиксации остановки не выполнялись.

Реализовано в production-коде:

- Dedicated enrollment с authenticated descriptor, capabilities/версиями, nonce/CSR/PoP, лимитами, same-origin transport, DNS/IP policy и запретом redirects. Accepted reserve/authorize/bind/admit связаны с прежними IDs/key/digests; signing journal сохраняет DER до ответа и возвращает прежние DER по IssueId.
- Новый ограниченный P-256 application CA, защищённый issuer key, schema-3 trust registry и application revocation на peer boundary. Genesis intent сохраняется до native эффекта; exact permit разрешает только InitializeCluster и исходный BootstrapAccount.
- Новый первый Bootstrap: quarantine singleton → accepted baseline/account → fresh majority barrier → process/activation/enable → durable отзыв setup → CommonOpen. Отдельный общий администратор не подменяется setup identity. Реальная выдача приглашений после этого исправлена с schema2 на текущую schema3 и проверена через production dispatcher/WAL/reducer.
- Второй узел: E1–E4 trust/admission, protected content-addressed trust manifest, SQL intent до файла, сверка refs/identity/registry и обе peer TLS-пробы. Узкая pending session разрешает только probe, не Raft/commands/common. Полный Join и его runtime остаются закрыты до snapshot/membership orchestration 05.
- Process-bound resume с новым BootChallenge/ProcessEpoch и ожиданием прежней lease. ManagedRestart нового первого использует AEAD storage seal, snapshot/SQL checkpoint и карантинный startup до majority; новый barrier/Enable обязателен. Старые grants не восстанавливаются, offline recovery/maintenance не расширены.
- Миграция `20261103105437_AddEnrollmentTrust` сгенерирована по отдельному разрешению без применения к БД. Конфигурация установки и завершённые 01/02/03 не изменялись.

Точные текущие контракты — [Onboarding contracts](../../../Technical%20documentation/Onboarding%20contracts.md), проверки и команды — [Testing](../../../Technical%20documentation/Testing.md#точка-остановки-этапа-04-2026-11-03). Последние результаты: Web 28 успешных + отдельно ManagedInvitation 1 успешный; compile-check Web/Infrastructure test projects успешен. Installation: 185/186 успешных, ошибка preflight исправлена, **после исправления сборка и повтор теста не выполнены**. Все обязательные критерии этапа этим не подтверждены.

### Возобновление 2026-11-15

[04.01](04.01-preflight-recheck.md) завершён: Infrastructure.Tests с зависимостями пересобран без предупреждений/ошибок, точный SetupPreflightTests-фильтр — 13 успешно, 0 ошибок/пропусков. Незакрытая проверка preflight подтверждена; код не менялся. Весь Installation и Web повторно не запускались, внешняя приёмка остаётся открытой. Следующая задача — [04.02](04.02-restart-mode-before-effects.md). [Команды и ограничения](../../../Technical%20documentation/Testing.md#повтор-preflight-0401-2026-11-15).

В [04.02](04.02-restart-mode-before-effects.md) Mode=Restart перенесён до эффектов и чтения storage. Добавлены семь негативных вариантов и усилен успешный Restart-тест. Production Infrastructure и Web.Tests пересобраны без предупреждений/ошибок. По просьбе пользователя выполнен только ManagedRestartTests: 13 успешно, 0 ошибок/пропусков; 04.02 завершён.

В [04.03](04.03-unbounded-progress-validation.md) снят общий предел 10000 шагов: история проверяется страницами по 256 с keyset по Id и прежними identity/digest/revision проверками. Регрессионный тест подтвердил прежний отказ на 10001 шаге; после правки точный ProgressValidationTests-фильтр — 32 успешно, 0 ошибок/пропусков. Production Infrastructure и Infrastructure.Tests собраны без предупреждений/ошибок. SQL translation проверена без выполнения SQL, реальная БД не проверялась.

[04.04](04.04-storage-seal-lifecycle.md) завершён: исходные WAL state/checkpoint проверяются до native open, seal запрещает откат term и конфликтующий head того же index, сбой фоновой публикации закрывает accepted recovery. Concurrent apply/snapshot и crash boundaries проверены изолированно; финальные затронутые фильтры — Web 42 и Infrastructure RevisionRecovery 6 успешно, без ошибок/пропусков. Стоимость полного snapshot на каждый apply сохранена и описана; нагрузка/fsync и полный Join не подтверждены.

[04.05](04.05-bootstrap-and-invitation-proof.md) завершён в изолированном объёме: добавлены 17 проверок потери семи native ответов, commit отзыва setup до CommonOpen, отказа Ready, sealed restart со старым genesis/account и schema3 приглашений. Финальный затронутый Web-фильтр — 59 успешно, 0 ошибок/пропусков; последняя локальная правка типов в invitation tests проверена отдельным build и 5/5. Production код не менялся. Неполный первый старт без seal безопасно отказывает; автоматическое resume этой границы остаётся открытым crash/reconciliation объёмом, а не результатом 04.05.

[04.06](04.06-enrollment-through-e4.md) завершён: прослежена и проверена production цепочка E1–E4. Исправлен приём подменённых IssueId/template/DER и недостаточная проверка ограниченного admitted descriptor; read-state/process-resume/renew до Bind сохраняют прежний DER. Проверены потеря reserve, обрыв Bind и каждого направления peer, порядок SQL intent → trust file, resume с прежними IDs/key/digests и закрытые Raft/commands/snapshot/common при активной pending TLS session. Финальные точные фильтры: Infrastructure 100 + отдельно limiter/compatibility 4 и Web 13 успешно, 0 ошибок/пропусков. Production Infrastructure/Web и оба test-проекта собраны без предупреждений/ошибок. Реальная сеть/БД/Ubuntu не проверялись; full Join остаётся закрыт. [Команды и границы](../../../Technical%20documentation/Testing.md#enrollment-e1e4-0406-2026-11-15).

Повторная проверка именно 04.06 на текущей рабочей копии подтвердила те же 104 Infrastructure и 13 Web без ошибок/пропусков и четыре compile-check без предупреждений/ошибок. Устранён отсутствующий using в ранее оставленном EnrollmentRoutingSecurityTests, блокировавший общую сборку Web.Tests; production-код не менялся. Результаты 04.01–04.05 и существующие результаты 04.07 сохранены. Изменения 04.08 остаются незавершёнными и этим поручением не приняты. Этап 04 остаётся `in-progress`.

### Остаток собственного объёма 04

В [04.07](04.07-existing-cluster-issuer.md) подключён первоначальный issuer ручного кластера: новый локальный dedicated CA, fresh accepted Administrator, прежний Pending/pipeline, capability/schema каждого voter и majority до регистрации и после apply. Повтор сохраняет ключ/DER/команду/результат; signer закрыт до exact accepted issuance. Legacy CA/pins сохранены, structured Registered/Rejected завязаны на accepted итог. Проверки: 47 новых +35 затронутых Web и 7 Infrastructure — без ошибок/пропусков; production/test compile-check без warnings/errors. [Команды и ограничения](../../../Technical%20documentation/Testing.md#issuer-ручного-кластера-0407-2026-11-15).

1. Remote issuer/follower routing и orchestration Retire/Revoke/replacement issuer. Первоначальный manual provisioning/Register завершён в 04.07; accepted reducers остальных команд не заменяют полный работающий сценарий. Не импортировать root/operator key и не менять конфигурацию установки.
2. Полный leaf renewal/rotation/revocation/retirement, новый ключ, exact authorization и overlap ≤24 часов. Текущий signer связан с активной onboarding attempt; одного endpoint renew недостаточно.
3. Законные online process/activation/enable exchanges последующих voters и ManagedRestart завершённого Join, включая удалённого signer. Snapshot/membership orchestration остаётся 05; нельзя создавать startup cycle всех voters или открывать gate до доказательства.
4. Завершить negative/crash matrix: TTL на границе секунды, nonce/identity/digest/revocation/replay, SQL/file/WAL reconciliation и crash boundaries. Исправленный preflight повторён в 04.01; дальнейшие проверки выполнять точными безопасными фильтрами, не весь Infrastructure.
5. Подключить минимальный Join UI к существующему `/api/setup/enroll`, очистку invitation и достоверный статус E4 без фиктивного Ready. На остановке только прочитаны setup.ts/cshtml и frontend tests; UI не изменён. Полный мастер/список узлов остаются 06.
6. Согласовать все актуальные документы/AGENTS с кодом, проверить ссылки/solution items/UTF-8/EOL, размер новых C# файлов и разнести несколько record из `EnrollmentPeerProbe.cs` по принятому стилю. Прежние результаты 03 не выдавать за регрессию 04.

### Известные проблемы и ограничения

- `ManagedStorageSeals` после каждого дальнейшего durable apply публикует полный snapshot/checkpoint/seal. В 04.04 проверены interleaving и crash boundaries, линейная полному образу стоимость описана без уменьшения частоты. Production throughput/fsync не измерены; native commit ниже SQL/seal после сбоя требует offline recovery.
- До первого seal прерванный Bootstrap не продолжает native startup автоматически. В 04.05 подтверждён безопасный отказ без второго genesis/account; восстановление произвольных ранних границ остаётся в crash/reconciliation объёме 04.
- Согласование документации не завершено: в актуальных документах/UI могут оставаться старые фразы о полностью закрытом production Bootstrap. Настоящая граница — этот результат и вводные разделы Onboarding contracts/Local installation setup; UI пока не отражает её полностью.
- Реальные Ubuntu, сетевые TLS handshakes, ACL/fsync/power loss, SQL/DDL/transaction locking, выборы/majority и переключение живого окружения не проверены. Doubles, local WAL и криптография этого не доказывают.

После завершения 04.01–04.07 ближайший незавершённый пункт — 04.08: в рабочей копии есть ранее оставленные изменения, статус `in-progress`; проверка 04.06 не завершает и не продолжает его реализацию. Затем остаётся собственный объём 04. Перенос 03а/03б, полный Join 05, sender 05а и экраны 06 не реализованы и не начинались. Отсутствие Telegram-токена на несендере не является препятствием допуска.

## Подэтапы

Этап частично реализован, работа возобновлена с 04.01. Статус каждой задачи показывает её собственный объём: наличие части кода не закрывает оставшиеся проверки.

| Подэтап | Зависимости | Статус |
| --- | --- | --- |
| [04.01. Повтор незакрытой проверки preflight](04.01-preflight-recheck.md) | Требования этапа | `completed` |
| [04.02. Mode-проверка ManagedRestart до эффектов](04.02-restart-mode-before-effects.md) | [04.01](04.01-preflight-recheck.md) | `completed` |
| [04.03. Проверка растущей истории steps](04.03-unbounded-progress-validation.md) | [04.01](04.01-preflight-recheck.md) | `completed` |
| [04.04. Storage seals: конкурентность и стоимость](04.04-storage-seal-lifecycle.md) | [04.01](04.01-preflight-recheck.md), [04.02](04.02-restart-mode-before-effects.md), [04.03](04.03-unbounded-progress-validation.md) | `completed` |
| [04.05. Новый первый: Bootstrap и выдача приглашения](04.05-bootstrap-and-invitation-proof.md) | [04.02](04.02-restart-mode-before-effects.md), [04.04](04.04-storage-seal-lifecycle.md) | `completed` |
| [04.06. Enrollment E1–E4 и двусторонние probes](04.06-enrollment-through-e4.md) | [04.01](04.01-preflight-recheck.md) | `completed` |
| [04.07. Issuer для существующего ручного кластера](04.07-existing-cluster-issuer.md) | [04.05](04.05-bootstrap-and-invitation-proof.md) | `completed` |
| [04.08. Удалённый issuer и follower-координатор](04.08-remote-issuer-routing.md) | [04.06](04.06-enrollment-through-e4.md), [04.07](04.07-existing-cluster-issuer.md) | `in-progress` |
| [04.09. Renewal и ротация leaf с новым ключом](04.09-leaf-renewal-and-overlap.md) | [04.08](04.08-remote-issuer-routing.md) | `not-started` |
| [04.10. Отзыв credentials и закрытие соединений](04.10-revocation-and-pool-invalidation.md) | [04.09](04.09-leaf-renewal-and-overlap.md) | `not-started` |
| [04.11. Replacement, отзыв и retirement issuer](04.11-issuer-replacement-and-retirement.md) | [04.07](04.07-existing-cluster-issuer.md), [04.08](04.08-remote-issuer-routing.md), [04.09](04.09-leaf-renewal-and-overlap.md), [04.10](04.10-revocation-and-pool-invalidation.md) | `not-started` |
| [04.12. Online exchanges последующих voters](04.12-online-process-exchanges.md) | [04.08](04.08-remote-issuer-routing.md), [04.10](04.10-revocation-and-pool-invalidation.md) | `in-progress` |
| [04.13. ManagedRestart завершённого Join](04.13-joined-managed-restart.md) | [04.04](04.04-storage-seal-lifecycle.md), [04.12](04.12-online-process-exchanges.md) | `not-started` |
| [04.14. Негативная матрица протокола и сроков](04.14-negative-protocol-matrix.md) | [04.06](04.06-enrollment-through-e4.md), [04.09](04.09-leaf-renewal-and-overlap.md), [04.10](04.10-revocation-and-pool-invalidation.md), [04.11](04.11-issuer-replacement-and-retirement.md), [04.12](04.12-online-process-exchanges.md), [04.13](04.13-joined-managed-restart.md) | `not-started` |
| [04.15. Crash: SQL, файлы, WAL и DER journal](04.15-crash-reconciliation.md) | [04.04](04.04-storage-seal-lifecycle.md), [04.06](04.06-enrollment-through-e4.md), [04.08](04.08-remote-issuer-routing.md), [04.12](04.12-online-process-exchanges.md), [04.13](04.13-joined-managed-restart.md) | `in-progress` |
| [04.16. Минимальный Join UI до E4](04.16-minimal-enroll-ui.md) | [04.06](04.06-enrollment-through-e4.md) | `not-started` |
| [04.17. Актуальные документы и форма новых типов](04.17-current-docs-and-code-shape.md) | [04.02](04.02-restart-mode-before-effects.md), [04.03](04.03-unbounded-progress-validation.md), [04.04](04.04-storage-seal-lifecycle.md), [04.05](04.05-bootstrap-and-invitation-proof.md), [04.06](04.06-enrollment-through-e4.md), [04.07](04.07-existing-cluster-issuer.md), [04.08](04.08-remote-issuer-routing.md), [04.09](04.09-leaf-renewal-and-overlap.md), [04.10](04.10-revocation-and-pool-invalidation.md), [04.11](04.11-issuer-replacement-and-retirement.md), [04.12](04.12-online-process-exchanges.md), [04.13](04.13-joined-managed-restart.md), [04.16](04.16-minimal-enroll-ui.md) | `not-started` |
| [04.18. Проверки и приёмка собственного объёма 04](04.18-stage-acceptance.md) | [04.01](04.01-preflight-recheck.md), [04.14](04.14-negative-protocol-matrix.md), [04.15](04.15-crash-reconciliation.md), [04.16](04.16-minimal-enroll-ui.md), [04.17](04.17-current-docs-and-code-shape.md) | `not-started` |

**04.01–04.07 завершены**; ближайший незавершённый шаг — **04.08** (`in-progress` в сохранённой рабочей копии). Текущее поручение ограничено повторной проверкой 04.06. Дальше выбирать только задачу с выполненными зависимостями; 04.18 закрывает собственный объём доверия, без переноса работ из 03b/05/05a/06.
