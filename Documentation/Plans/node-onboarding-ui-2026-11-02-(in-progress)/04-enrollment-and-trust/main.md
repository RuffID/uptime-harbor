# 04. Установление доверия

Статус: `in-progress`. Работа приостановлена по просьбе пользователя 2026-11-03, этап не завершён. Зависимости: [01](../01-scenarios-and-decisions/main.md)–[03](../03-initial-setup/main.md).

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

### Остаток собственного объёма 04

1. Production provisioning/Register/Retire/Revoke/replacement issuer существующего ручного кластера и удалённый issuer/follower: accepted reducers не заменяют полный работающий сценарий. Не импортировать root/operator key и не менять конфигурацию установки.
2. Полный leaf renewal/rotation/revocation/retirement, новый ключ, exact authorization и overlap ≤24 часов. Текущий signer связан с активной onboarding attempt; одного endpoint renew недостаточно.
3. Законные online process/activation/enable exchanges последующих voters и ManagedRestart завершённого Join, включая удалённого signer. Snapshot/membership orchestration остаётся 05; нельзя создавать startup cycle всех voters или открывать gate до доказательства.
4. Завершить negative/crash matrix: TTL на границе секунды, nonce/identity/digest/revocation/replay, SQL/file/WAL reconciliation и crash boundaries. Повторить исправленный preflight и затем необходимые точные безопасные фильтры, не весь Infrastructure.
5. Подключить минимальный Join UI к существующему `/api/setup/enroll`, очистку invitation и достоверный статус E4 без фиктивного Ready. На остановке только прочитаны setup.ts/cshtml и frontend tests; UI не изменён. Полный мастер/список узлов остаются 06.
6. Согласовать все актуальные документы/AGENTS с кодом, проверить ссылки/solution items/UTF-8/EOL, размер новых C# файлов и разнести несколько record из `EnrollmentPeerProbe.cs` по принятому стилю. Прежние результаты 03 не выдавать за регрессию 04.

### Известные проблемы и ограничения

- `ManagedRestartPreparation.PrepareAsync` проверяет Mode=Restart в конце, после Publish/TLS install; перенести проверку до эффектов при возобновлении.
- `LocalSetupAccountStore.ValidateProgressAsync` ограничен 10000 steps; требуется безопасная проверка растущей истории, иначе долгоживущая установка может получить отказ.
- `ManagedStorageSeals` после каждого дальнейшего durable apply публикует snapshot/checkpoint/seal. Это консервативный текущий механизм; стоимость и конкурентность/crash boundaries ещё не оценены. Не ослаблять rollback checks ради ускорения.
- Согласование документации не завершено: в актуальных документах/UI могут оставаться старые фразы о полностью закрытом production Bootstrap. Настоящая граница — этот результат и вводные разделы Onboarding contracts/Local installation setup; UI пока не отражает её полностью.
- Реальные Ubuntu, сетевые TLS handshakes, ACL/fsync/power loss, SQL/DDL/transaction locking, выборы/majority и переключение живого окружения не проверены. Doubles, local WAL и криптография этого не доказывают.

Начать возобновление с незакрытой проверки preflight и перечисленных известных проблем; затем завершать собственный остаток 04. Перенос 03а/03б, полный Join 05, sender 05а и экраны 06 не реализованы и не начинались. Отсутствие Telegram-токена на несендере не является препятствием допуска.

## Подэтапы

Этап частично реализован и приостановлен. Статус каждой задачи показывает её собственный объём: наличие части кода не закрывает оставшиеся проверки. Подготовка этих файлов не возобновляет реализацию.

| Подэтап | Зависимости | Статус |
| --- | --- | --- |
| [04.01. Повтор незакрытой проверки preflight](04.01-preflight-recheck.md) | Требования этапа | `verification` |
| [04.02. Mode-проверка ManagedRestart до эффектов](04.02-restart-mode-before-effects.md) | [04.01](04.01-preflight-recheck.md) | `not-started` |
| [04.03. Проверка растущей истории steps](04.03-unbounded-progress-validation.md) | [04.01](04.01-preflight-recheck.md) | `not-started` |
| [04.04. Storage seals: конкурентность и стоимость](04.04-storage-seal-lifecycle.md) | [04.01](04.01-preflight-recheck.md), [04.02](04.02-restart-mode-before-effects.md), [04.03](04.03-unbounded-progress-validation.md) | `in-progress` |
| [04.05. Новый первый: Bootstrap и выдача приглашения](04.05-bootstrap-and-invitation-proof.md) | [04.02](04.02-restart-mode-before-effects.md), [04.04](04.04-storage-seal-lifecycle.md) | `in-progress` |
| [04.06. Enrollment E1–E4 и двусторонние probes](04.06-enrollment-through-e4.md) | [04.01](04.01-preflight-recheck.md) | `in-progress` |
| [04.07. Issuer для существующего ручного кластера](04.07-existing-cluster-issuer.md) | [04.05](04.05-bootstrap-and-invitation-proof.md) | `not-started` |
| [04.08. Удалённый issuer и follower-координатор](04.08-remote-issuer-routing.md) | [04.06](04.06-enrollment-through-e4.md), [04.07](04.07-existing-cluster-issuer.md) | `not-started` |
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

После отдельного поручения начать с **04.01**, затем исправить известные проблемы **04.02–04.04**. Дальше выбирать только задачу с выполненными зависимостями; 04.18 закрывает собственный объём доверия, без переноса работ из 03b/05/05a/06.
