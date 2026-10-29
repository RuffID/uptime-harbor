# Связь узлов, Raft и готовность

## Реализованная граница этапа 03

`Application/Cluster` содержит независимые recovery/accepted-state/trust/consensus контракты, readiness, membership coordinator, окно подтверждённой готовности главного и sender gate. `Infrastructure/Cluster` содержит DotNext adapter, state machine boundary, защиту дискового состояния, mTLS/envelope/replay и probes. `Web/Cluster` управляет отдельным peer-периметром и жизненным циклом DotNext.

Реальные прикладные команды, reducer, установка общего snapshot, SQL replay и проверка догона принадлежат 04; принятый активный аккаунт — 05; workers — 06/07; UI и включение — 08/09. По умолчанию `UnavailableClusterRecovery`, `UnavailablePeerTrustSource`, `UnavailableClusterAdministrationState` и `UnavailableSenderConsensus` ничего не подтверждают. Это закрытые границы подключения, а не успешные заглушки. До их реализации enabled-конфигурация не создаёт WAL и не запускает Raft.

`Disabled` сохраняет однопроцессное поведение. Любой enabled-режим не регистрирует прежние monitoring/outbox workers, блокирует прежний `INotificationSender` и browser endpoints, пропускает только peer-perimeter. Legacy startup migrations/bootstrap тоже пропускаются: создание БД/первого аккаунта не подменяет восстановление сетки. Наличие классов этапа 03 не разрешает controlled enable.

## Долговременный Raft

Используются `DotNext.Net.Cluster` и `DotNext.AspNetCore.Cluster` **6.10.0**, upstream commit `3499aedecf58b3c27a5d87af700d99e6b973cfde`. Majority commit, prevote/election, term и step-down выполняет DotNext. Production adapter вызывает `IReplicationCluster<IRaftLogEntry>.ReplicateAsync`, затем ждёт фактического apply через Context и `WaitForApplyAsync`; собственных Append/Commit или второго алгоритма согласования в adapter нет.

Порядок старта:

1. Проверить локальные БД и часы. `IClusterRecovery.RestoreAsync` обязан подтвердить identity/storage generation, отсутствие клона/fencing и checkpoint, восстановить snapshot **до** создания `WriteAheadLog`.
2. Захватить эксклюзивный `instance.lock`, проверить офлайн provisioned `manifest.json`. Только первый `Bootstrap` с непустым BootstrapId разрешает ColdStart. Маркер `started` записывается с flush; повторный запуск требует прежних `wal/state` и `membership`. Отсутствующие/усечённые файлы не превращают старый узел в новую сетку.
3. Создать отдельный DI-контейнер DotNext без запуска host. `WriteAheadLog` использует CRC64, `FlushInterval=0`, `FlushOnCommit=true`; term/vote/log и persistent configuration расположены под отдельным `StoragePath`, вне SQL и wwwroot.
4. Инициализировать WAL/replay; `ConfirmLocalReplayAsync` должен проверить committed/applied index и persisted membership version. Только затем вызвать DotNext Start и открыть native RPC. Неподтверждённый replay закрывает старт.

Storage имеет процессный lock; доверенная пара NodeId–URI и storage generation не допускают второго адреса с тем же NodeId. Копирование всей машины вместе с ключами/lock-файлами на другой диск не решается локальным FileShare.None: recovery-порт обязан потребовать внешний fencing, а 09 — обеспечить процедуру. Полный клон не признаётся безопасным автоматически. Прерванный первый bootstrap с `started`, но неполным WAL требует отдельного восстановления; автоматической очистки нет.

Новая provisioned сетка начинает с одного голосующего и большинства 1. Restart сохраняет принятый состав; отсутствие двух узлов трёхузловой сетки не позволяет пересоздать singleton. Failure detector с удалением участников не регистрируется. Состав меняется по одному через DotNext Add/Remove, с проверкой persisted version под lock, leader/consensus cancellation и request timeout. Сохраняемый membership не выводится из health.

`MembershipCoordinator` требует достоверный UserId, локальный active Administrator и подтверждённое принятое право/trust/предыдущую сверку из `IClusterAdministrationState`. Запрещены удаление последнего, подмена адреса и клон NodeId. Перед операцией закрывается sender, выдерживается 22 с drain; после операции нужны strong read barrier, ожидаемый состав/версия и подтверждение принятого результата. При неопределённости sender остаётся закрыт до сверки и нового принятого sender epoch. Логи содержат только actor/node/change IDs и закрытый outcome; успех не пишется до подтверждения. Создания/отзыва trust credentials через старую cookie нет: изменения trust должны поступать из будущего принятого журнала 04/05.

## Peer transport

Peer host и локальный порт отделены от browser host/порта. `ConfigureClusterListeners` требует именованные Kestrel endpoints `Peer` и `Browser`, заранее проверяет локальный client key/certificate/CA и SAN identity. Прямой HTTPS требует client certificate; цепочка проверяется по закреплённому private CA с clientAuth/revocation. Middleware до forwarded headers и cookie authentication дополнительно проверяет SHA-256 pin принятого credential, SAN URI `urn:uptimeharbor:{ClusterId}:{Generation}:{NodeId}`, validity и accepted trust.

Для `NginxIsolatedHttp` TLS/mTLS завершается на отдельном Nginx peer virtual host. Внутренний HTTP доступен только Nginx через loopback/изолированную сеть; firewall и запрет прямого ingress обязательны. Приложение доверяет certificate headers только от точного IP allowlist на peer-порту, при `Ssl-Client-Verify: SUCCESS`; URL-encoded `Ssl-Client-Cert` содержит публичный PEM. Nginx должен перезаписывать эти headers, проверять CA/CRL и отключать TLS early data. Обычные forwarded headers такого доверия не дают. Browser virtual host не запрашивает client certificate. Проверка server hostname/публичного TLS certificate в исходящем клиенте не отключена.

`X-UptimeHarbor-Peer` ограничен 4096 символами; содержит ClusterId/generation, sender/recipient NodeId, storage generation, CredentialId, compatibility, RequestId, timestamp и challenge. Совместимость строго `1.0/1/1/1/1/6.10.0`; время ±30 с. Ответ обязан совпасть по request/challenge и принятой идентичности. Cookies/Authorization, redirects, proxy и HTTP logging исходящего peer-клиента отключены. Native `X-Raft-Node-ID` связан с accepted URI, term/state version проверяются отдельно; admission не имеет права голосовать. Native retries остаются в журнале DotNext.

Прикладной health nonce сохраняется на 2 минуты в эксклюзивном локальном inbox с flush/atomic replace; дубль получает 409. Nonce не является бизнес-идемпотентностью. Health ограничен 64 KiB, application entry — 256 KiB; envelope/options фиксируют batch 1 MiB/100 и snapshot chunk 1 MiB для будущего 04. Native HTTP имеет отдельный предел 64 MiB: это не прикладной batch/chunk API. Kestrel ограничивает входящий поток, health reader ограничивает фактически прочитанные байты. Ответ native ошибки не передаёт текст исключения. Peer health содержит только reason codes, IDs, term/indices и возраст, без credentials, token и payload.

Доменные сертификаты обновляются независимо от NodeId. Ротация client CA/credential требует принятого trust и процедуры 04/09; секретный CA key не находится на узлах, приватные ключи не реплицируются. Реальная настройка Nginx/CRL/renewal не выполнялась.

## Диагностика и lifecycle

`NodeReadiness` хранит известный результат и monotonic age. Просроченный результат становится Unknown/Stale. `ClusterDatabaseProbe` проверяет собственную БД с timeout; отказ немедленно закрывает CanWork/CanSend и новые ACK/vote/writers. Supervisor закрывает RPC и останавливает DotNext, сохраняя файлы и состав. Старое доказательство recovery инвалидируется. После ремонта нужны restore/replay и новое подтверждение accepted applied prefix; прежняя sender authority не восстанавливается. Восстановленный узел начинает обычное участие как follower, роль далее определяет DotNext.

Отсутствие peer-ответа означает только PeerUnavailable, без утверждения об удалённой БД/боте. Результаты прямых polls имеют возраст; health-quorum требует одинаковых term/membership/leader и свежих взаимных прямых подтверждений одного большинства. Одна жалоба, односторонняя связь или partial-доступность обычной monitoring-цели не дают права сменить исправного главного.

Apply fault в state machine сохраняется отдельно от connectivity probe: успешный CanConnect не снимает запрет native ACK/vote. Снять fault может только новый Restore/replay lifecycle. Исключение reducer/provider заменяется безопасным сообщением без исходного inner payload до передачи DotNext и HTTP middleware.

`TelegramHealthProbe` использует только `getMe`, максимум раз в минуту, timeout 5 с, body 64 KiB, health lifetime 90 с; BotId должен совпасть с configured ожидаемым общим ботом. Чатовые Forbidden/ChatNotFound/InvalidChat и rate limit не объявляются общим outage. `getMe` не доказывает доступность каждого чата и не отправляет тестовых сообщений. Готовность worker не выводится из HTTP 200: `MonitoringProgressWatchdog` ждёт реальный завершённый цикл; будущий worker 06 обязан подключить этот контракт.

## Главный и sender authority

Таймеры DotNext: election 30000–36000 мс, HeartbeatThreshold=1/6, RPC 3 с, request 10 с, AggressiveLeaderStickiness=true, встроенная lease выключена. Health polls — 5 с. Последнее подтверждение готовности обновляет только свежий matching ready proof текущего лидера. Без успеха 30 с одно и то же подтверждённое большинство инициирует relinquish и блокирует старые RPC лидера этого term; дальше выбирает DotNext. Неуспешные проверки окно не сдвигают. При известной собственной неисправности gate закрывается и лидер resign немедленно. Это не норматив срока избрания/восстановления.

`SenderAuthorityGate` требует текущие cluster/generation/term/membership/sender epoch, leader и consensus cancellation tokens, accepted account/snapshot/catch-up/activation/manual enable/delivery state и свежую readiness. Ни Leader, ни локальная lease сами по себе недостаточны. После смены роли/epoch/состава — drain 12 с; renewal с уникальным round должен быть принят/применён, срок считается от начала запроса и не превышает 10 с. Следующий этап обязан обновлять authority примерно раз в 5 с. BeginAttempt тоже требует принятого applied результата, а финальная проверка выполняется не позднее 2 с от начала. Потеря условий закрывает новые попытки и отменяет in-flight scopes best effort; внешняя отправка не атомарна с отменой.

Monotonic watchdog проверяется каждую секунду; pause более 5 с или wall-clock drift более 2 с закрывает gate, clock fault требует сверки/перезапуска и повторного recovery. Обнаружение скачков не доказывает внешнюю синхронизацию часов и предел относительной скорости 1.1 из протокола. Authority не сохраняется в snapshot/SQL. В 03 нет renewal worker, reducer и transport доставки: реальные отправки остаются закрытыми.

## Проверка и пределы доказательств

Фактические команды и результаты — [Testing.md](Testing.md#проверка-этапа-03). Изолированные тесты вызывают production gate/perimeter/probes и настоящий DotNext WAL/configuration. Storage reopen не доказывает fsync при отключении питания; подмена replication API проверяет только wiring adapter, а не выборы или majority acceptance. Не выполнялись настоящие peer TLS/Nginx, multi-node elections/partitions, процессы/OS pause, SQL recovery, Telegram и deployment. Эти наблюдения учитываются отдельно в этапе 10; рабочий ввод здесь не включён.
