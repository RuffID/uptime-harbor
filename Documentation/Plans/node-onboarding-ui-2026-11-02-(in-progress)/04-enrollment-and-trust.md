# 04. Установление доверия

Статус: `not-started`. Зависимости: [01](01-scenarios-and-decisions.md)–[03](03-initial-setup.md).

Граница после 03: заменить закрытые production ISetupAuthority/IManagedRuntimeAdmission настоящими адаптерами, сохранив local step CAS, исходные IDs/digests и E0 private key. LocalInstallationProfile уже содержит ClusterGeneration/CredentialId/TrustAnchorFingerprint; динамические TLS/trust providers не предоставляют authority сами. Двусторонние probes, accepted ProcessEpoch, genesis/issuer/grants/activation ещё не реализованы. [Фактический setup](../../Technical%20documentation/Local%20installation%20setup.md).

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

Реализовать [протокол issuer/E0–E8/grants/validators](technical-design.md#enrollment). Новый dedicated pathLen=0 application CA создаётся первым автоматически, закрытый ключ остаётся только issuer node; существующий root/offline operator key не импортировать. GenesisAuthorization либо accepted RegisterEnrollmentIssuer разрывают цикл начального доверия. Browser HTTPS authenticates origin, CSR PoP/accepted template/Bind fingerprint дают конкретный leaf. Durable IssueId возвращает те же DER; CA signature сама не предоставляет authority.

Tagged proof заменяет общий FencingConfirmed только в новых online ветках, включая ClusterAccountBootstrapper; offline signature/manual isolation/CRL/maintenance сохраняются. DotNextConsensusAdapter получает exact genesis writer permit без раннего общего CanWork и произвольных команд. ManagedRestart разрешает карантинный запуск ранее целого хранилища до большинства, затем новый barrier/Enable; restore/clone/removed identity остаются offline. Exact changes перечислены по классам; server name/pin/KU/EKU, application revocation на каждый RPC, renew/retire/issuer replacement и process-epoch replay tests обязательны. Сроки — [parameters](technical-design.md#parameters), события/негативные случаи — [verification](technical-design.md#verification).

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

Пока не выполнен. Доверенный обмен не означает готовность узла к работе: membership, snapshot/recovery и рабочие gates подтверждает этап 05.
