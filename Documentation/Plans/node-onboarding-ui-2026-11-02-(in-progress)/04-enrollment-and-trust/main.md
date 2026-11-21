# Шаг 1. Ручное подключение узлов

**Для MVP работа завершена.** Удалены веб-приглашения, мастер настройки и автоматическое восстановление после сбоя при смене сертификата. Ручной режим Legacy сохраняет допуск по сертификату, загрузку снимка, вступление в состав и синхронизацию.

Собраны затронутые проекты .NET и TypeScript. Прошли 174 теста .NET и 14 frontend-тестов. Проверены отказ при неверном сертификате, повторный ручной допуск, чтение снимка и отображение времени результата каждого из двух наблюдателей. Проверка в браузере на реальных узлах входит в [шаг 3](../07-verification-and-rollout/main.md).

Следующая работа — [ручное переключение Telegram](../05a-telegram-sender-and-backlog/main.md).

## Результат

Ниже сохранены результаты прежнего этапа 04 на дату выполнения. Работу над автоматизацией остановили внутри 04.09.04. Подробные назначения и проверки перечислены в [исторической сводке](progress.md).

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

### Известные проблемы и ограничения

- `ManagedStorageSeals` после каждого дальнейшего durable apply публикует полный snapshot/checkpoint/seal. В 04.04 проверены interleaving и crash boundaries, линейная полному образу стоимость описана без уменьшения частоты. Production throughput/fsync не измерены; native commit ниже SQL/seal после сбоя требует offline recovery.
- До первого seal прерванный Bootstrap не продолжает native startup автоматически. В 04.05 подтверждён безопасный отказ без второго genesis/account; восстановление произвольных ранних границ остаётся в crash/reconciliation объёме 04.
- Согласование документации не завершено: в актуальных документах/UI могут оставаться старые фразы о полностью закрытом production Bootstrap. Настоящая граница — этот результат и вводные разделы Onboarding contracts/Local installation setup; UI пока не отражает её полностью.
- Реальные Ubuntu, сетевые TLS handshakes, ACL/fsync/power loss, SQL/DDL/transaction locking, выборы/majority и переключение живого окружения не проверены. Doubles, local WAL и криптография этого не доказывают.

В задании `NUI-04-20261117-01` [04.08](04.08-remote-issuer-routing.md) завершён: проверен follower ingress/coordinator и удалённый signer с exact accepted template/barrier/epoch; outage/lost response/смена leader и concurrent IssueId сохраняют identity/DER без обхода Bind. Remote grants сохраняют unsigned intent/peer-key proof до RPC, не дают authority без accepted record/consumption. Исправлен replay intent при росте cut (MinimumIndex/время). Финальный прогон: Web 150 и Infrastructure 15 успешно, без ошибок/пропусков; четыре compile-check без предупреждений/ошибок. [Подробные доказательства](../../../Technical%20documentation/Testing.md#удалённый-issuer-и-follower-routing-0408-2026-11-17). Реальные TLS/SQL/Ubuntu/majority и UI не проверялись. Ближайший незавершённый пункт — 04.09; исполнитель дальше не переходил. Перенос 03а/03б, полный Join 05, sender 05а и экраны 06 этим не реализованы. Отсутствие Telegram-токена на несендере не является препятствием допуска.

В задании `NUI-04-20261117-02` завершена только **04.09-A**: accepted rotation state/Authorize/Bind/Retire/CredentialRevision, expiry/overlap/replay и snapshot/recovery invariants, historical DER validation и безопасный audit. Application 124 и Infrastructure 18 успешно, 0 ошибок/пропусков; четыре compile-check — 0 предупреждений/ошибок. Production rotation admission явно закрыт до B; runtime renewal/установка ключа/ContinueCredential не включены. [Результат и история](04.09-leaf-renewal-and-overlap.md#фактический-результат), [проверки и ограничения](../../../Technical%20documentation/Testing.md#voter-credential-rotation-0409a-2026-11-18). Новых migrations нет, БД/сеть/хост/Git не использовались. Родитель 04.09 остаётся `in-progress`.

В задании `NUI-04-20261118-03` выполнена ограниченная **04.09-B1**: независимые voter PoP/new CSR, exact accepted authorization/fresh majority, typed local/remote signer core с прежним lock/DER journal и повторной проверкой ответа. 96 новых B1 tests; итоговые Infrastructure 123 и Application 76 regression успешно, 0 ошибок/пропусков; четыре compile-check без предупреждений/ошибок. Production DI/HTTP/dispatcher не открыты. [Матрица и ограничения](../../../Technical%20documentation/Testing.md#voter-credential-authority-0409b1-2026-11-18). Ближайшее назначение B2, затем B3/C; весь 04.09 открыт.

## Подэтапы

В задании `NUI-04-20261118-04` выполнена только **B2a ДО Bind**. Реальные protected intent/key/authorization/DER files/reopen и fault/replay/concurrency проверены: **62 новых B2a**, финально **Infrastructure 187** (62+125 regression) и **Application 76 regression** успешно, 0 ошибок/пропусков; четыре compile-check без предупреждений/ошибок. [Доказательства/границы](../../../Technical%20documentation/Testing.md#voter-credential-local-prebind-0409b2a-2026-11-18). SQL/физический fsync/runtime/UI не доказаны, migrations нет. Ближайший шаг — **B2b** по [декомпозиции 04.09](04.09-leaf-renewal-and-overlap.md#ограниченная-декомпозиция): exact Bind/CredentialRevision, install/confirmation и interrupted recovery; B2/04.09 открыты, production dispatcher deny/DI/routes сохранены.

Завершённые подэтапы прежнего плана.

- [04.01. Повтор незакрытой проверки preflight](04.01-preflight-recheck.md)
- [04.02. Mode-проверка ManagedRestart до эффектов](04.02-restart-mode-before-effects.md)
- [04.03. Проверка растущей истории steps](04.03-unbounded-progress-validation.md)
- [04.04. Storage seals: конкурентность и стоимость](04.04-storage-seal-lifecycle.md)
- [04.05. Новый первый: Bootstrap и выдача приглашения](04.05-bootstrap-and-invitation-proof.md)
- [04.06. Enrollment E1–E4 и двусторонние probes](04.06-enrollment-through-e4.md)
- [04.07. Issuer для существующего ручного кластера](04.07-existing-cluster-issuer.md)
- [04.08. Удалённый issuer и follower-координатор](04.08-remote-issuer-routing.md)

Частичные результаты сохранились в материалах [04.09](04.09-leaf-renewal-and-overlap.md), [04.12](04.12-online-process-exchanges.md) и [04.15](04.15-crash-reconciliation.md).
