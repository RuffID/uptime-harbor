# Локальные control-файлы

Online путь 04 не подписывает control-файлы и не снимает blocked/maintenance. GenesisWriterPermit допускает только точные InitializeCluster/BootstrapAccount bytes/IDs; это не operator signature/isolation attestation. FreshBootstrap и ManagedRestart нового первого подключены к отдельным accepted Process/Activation/Enable validators. AEAD managed-storage.seal — только локальный checkpoint provenance, не ручной fencing и не offline recovery grant. Missing/truncated WAL, backup/restore/recovery/maintenance markers закрывают managed restart; команды ниже сохраняют прежний offline контракт. [Граница trust](Onboarding%20contracts.md#промежуточная-граница-этапа-04-2026-11-03).

Это контракт офлайн-инструмента и подписанного локального управления этапа 09. [Последовательности запуска](Cluster%20operations.md) и [восстановления](Cluster%20recovery.md) обязательны. Команды ниже — примеры точного синтаксиса для отдельно разрешённой операции, в задаче они не запускались. Абсолютные пути и примерные IDs заменить значениями выбранной установки; не использовать примерные доказательства из документа как фактические.

## Офлайн-команды

`--cluster-control` обрабатывается **до** WebApplication.CreateBuilder, чтения Config/config.json, DI/SQL/HTTP/hosted services. Неизвестная команда завершается с кодом 1 и безопасным сообщением. Файлы результата создаются только через CreateNew; каталог и ACL подготовить отдельно. Для новых результатов выбирать новые имена, не перезаписывать прежние разрешения.

```powershell
dotnet C:\UptimeHarbor\UptimeHarbor.Web.dll --cluster-control keygen C:\UH-Control\operator-private.pem C:\UH-Control\operator-public.pem
dotnet C:\UptimeHarbor\UptimeHarbor.Web.dll --cluster-control provision C:\UH-Control\node-options.json
dotnet C:\UptimeHarbor\UptimeHarbor.Web.dll --cluster-control bootstrap C:\UH-Control\bootstrap-template.json C:\UH-Control\bootstrap-keys C:\UH-Control\bootstrap-protected.json
dotnet C:\UptimeHarbor\UptimeHarbor.Web.dll --cluster-control checkpoint C:\UH-Control\node-options.json C:\UH-Archive\selected.snapshot SHA256_FROM_VERIFIED_COPY C:\UH-Control\checkpoint.json
dotnet C:\UptimeHarbor\UptimeHarbor.Web.dll --cluster-control sign C:\UH-Control\request.json C:\UH-Control\operator-private.pem C:\UH-Control\signed-request.json
dotnet C:\UptimeHarbor\UptimeHarbor.Web.dll --cluster-control recover C:\UH-Control\recovery-request.json
```

Keygen печатает SHA256 SPKI; его exact uppercase значение закрепляется в `ClusterControl.PublicKeyDigest`. Приватный ключ остаётся только у разработчика/специалиста. Provision принимает отдельный JSON **ClusterOptions без оболочки Cluster**, требует пустой StoragePath и Mode Bootstrap/Join, создаёт manifest и печатает настоящий DotNext RaftMemberId для PublicUri. Для аварийного пакета provision не нужен: recover создаёт собственный manifest. Checkpoint проверяет файл по digest/compatibility, извлекает SnapshotId/SnapshotDigest/MinimumCommittedIndex/MembershipVersion, `Nodes` и `Operations`. Он не выводит hashes/stamps аккаунтов или delivery payload; результат с публичными сертификатами — локальный служебный файл, не browser DTO. Сам snapshot содержит защищённые общие данные и требует ACL.

Пример `node-options.json` для нового первого узла:

```json
{
  "Mode": "Bootstrap",
  "ClusterId": "11111111-1111-4111-8111-111111111111",
  "Generation": 1,
  "NodeId": "22222222-2222-4222-8222-222222222222",
  "StorageGeneration": "33333333-3333-4333-8333-333333333333",
  "CredentialId": "44444444-4444-4444-8444-444444444444",
  "PublicUri": "https://first.example/internal/raft",
  "StoragePath": "C:\\UH-Data\\cluster",
  "ExpectedBotId": 123456789
}
```

Это только identity для офлайн-tool. Runtime дополнительно требует полную локальную конфигурацию TLS/CA/SQL/proxy и секретов по [Configuration.md](Configuration.md). `ClusterControl` содержит абсолютные Directory, PublicKeyPath, PublicKeyDigest и KeyRingDirectory; bootstrap keyring отдельный, локальный, с app discriminator `UptimeHarbor.ClusterControl/<NodeId:N>`. Пароль интерактивный, без echo/redirect, проходит штатные минимальные Identity PasswordOptions; в template и аргументах пароля нет.

```json
{
  "ClusterId": "11111111-1111-4111-8111-111111111111",
  "ClusterGeneration": 1,
  "OperationId": "55555555-5555-4555-8555-555555555555",
  "OriginNodeId": "22222222-2222-4222-8222-222222222222",
  "ActorUserId": "66666666-6666-4666-8666-666666666666",
  "ActorRevision": 1,
  "EntityId": "66666666-6666-4666-8666-666666666666",
  "EntityKind": 4,
  "Action": "BootstrapAccount",
  "BaseRevision": 0,
  "CreatedAtUtc": "2026-10-30T00:00:00Z",
  "Value": { "Name": "admin", "Enabled": true, "RoleId": "77777777-7777-4777-8777-777777777777" }
}
```

EntityKind=4 — Account. RoleId новый и единственный, имя роли задаёт существующая account projection как Administrator. Bootstrap инструмент добавляет PasswordHash/format/stamps и защищает точную RevisionCommand; `bootstrap-protected.json` содержит строку ProtectedBootstrapCommand. Её перенести в Admission request, не раскрывать/перехешировать команду заново после подписания. CreatedAtUtc и IDs должны относиться к выбранной операции.

## Подписанное локальное разрешение

Формат `request.json` — PascalCase, strict JSON без неизвестных полей. После карантинного старта взять **текущие** Challenge/RecoveryVersion/OperationsRevision/MaintenanceId из control/status.json. RequestId новый, время UTC; ExpiresAtUtc позже текущего времени, окно не больше 15 минут. Подпись ECDSA P-256/SHA256 защищает точные bytes; envelope содержит base64 Payload и Signature. Установка копирует подписанный envelope в одно из имён ниже, без изменения содержимого.

```json
{
  "RequestId": "88888888-8888-4888-8888-888888888888",
  "Challenge": "99999999-9999-4999-8999-999999999999",
  "ClusterId": "11111111-1111-4111-8111-111111111111",
  "Generation": 1,
  "NodeId": "22222222-2222-4222-8222-222222222222",
  "StorageGeneration": "33333333-3333-4333-8333-333333333333",
  "Action": "Enable",
  "IssuedAtUtc": "2026-10-30T00:00:00Z",
  "ExpiresAtUtc": "2026-10-30T00:10:00Z",
  "RecoveryVersion": 1,
  "OperationsRevision": 3,
  "MaintenanceId": "00000000-0000-0000-0000-000000000000"
}
```

| Имя | Action | Дополнительные условия |
| --- | --- | --- |
| admission.json | Admission | Admission, Isolation; bootstrap только ProtectedBootstrapCommand |
| activate.json | Activate | текущие RecoveryVersion и OperationsRevision; одно разрешение не заменяет health/barrier |
| enable.json | Enable | текущие RecoveryVersion/OperationsRevision, MaintenanceId пуст; RequestId потребляется один раз |
| resume.json | ResumeConsensus | точный persisted MaintenanceId; разрешает только восстановить consensus, не workers |

Enable связывается с текущим запуском и RecoveryVersion; после принятия он действует до invalidate/maintenance/blocked, а не как таймер на 15 минут. Повторно использовать тот же разрешительный файл после restart нельзя: challenge меняется. Файл `blocked` в control directory немедленно закрывает локальный control; его удаление само по себе не даёт нового рабочего разрешения. Marker `maintenance` не удалять вручную: после ResumeConsensus и accepted LeaveMaintenance его снимает только новый Enable. Не использовать служебные файлы для обхода проверки схемы/apply fault.

Для Admission добавить поля:

```json
{
  "Admission": {
    "ClusterId": "11111111-1111-4111-8111-111111111111",
    "Generation": 1,
    "NodeId": "22222222-2222-4222-8222-222222222222",
    "StorageGeneration": "33333333-3333-4333-8333-333333333333",
    "FencingConfirmed": true,
    "OldBackup": false,
    "SnapshotId": null,
    "SnapshotDigest": null,
    "MinimumCommittedIndex": 0,
    "MembershipVersion": 0,
    "MonitoringNodes": {
      "22222222-2222-4222-8222-222222222222": "33333333-3333-4333-8333-333333333333"
    },
    "Operations": {
      "Nodes": {
        "22222222-2222-4222-8222-222222222222": {
          "NodeId": "22222222-2222-4222-8222-222222222222",
          "StorageGeneration": "33333333-3333-4333-8333-333333333333",
          "Address": "https://first.example/internal/raft",
          "RaftMemberId": "EXACT_PROVISION_STDOUT",
          "Voting": true, "Activated": false, "Removed": false, "Revision": 0
        }
      },
      "Credentials": {
        "44444444-4444-4444-8444-444444444444": {
          "Id": "44444444-4444-4444-8444-444444444444",
          "NodeId": "22222222-2222-4222-8222-222222222222",
          "Fingerprint": "EXACT_CERTIFICATE_SHA256",
          "PublicCertificate": "ACTUAL_PUBLIC_PEM_WITH_JSON_ESCAPED_NEWLINES",
          "ExpiresAtUtc": "ACTUAL_CERTIFICATE_NOT_AFTER_UTC",
          "AdmissionId": null, "AdmissionExpiresAtUtc": null
        }
      },
      "BotId": 123456789, "MembershipVersion": 0
    }
  },
  "ProtectedBootstrapCommand": "EXACT_PROTECTED_OUTPUT",
  "Isolation": {
    "ProcedureId": "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa",
    "OperatorId": "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb",
    "VerifiedAtUtc": "2026-10-30T00:00:00Z",
    "Instances": [{
      "NodeId": "22222222-2222-4222-8222-222222222222",
      "StorageGeneration": "33333333-3333-4333-8333-333333333333",
      "Stopped": true, "AutostartDisabled": true, "PeerIngressBlocked": true,
      "BrowserIngressBlocked": true, "CopiesQuarantined": true,
      "EvidenceDigest": "SHA256_OF_ACTUAL_EXTERNAL_RECORD"
    }],
    "BotId": 123456789,
    "PreviousBotTokenRevoked": false,
    "PreviousPeerTrustRevoked": false,
    "ExternalRecordDigest": "SHA256_OF_ACTUAL_PROCEDURE_RECORD"
  }
}
```

Это добавляемые поля **того же** запроса Action=Admission, не отдельный envelope. Действующий локальный процесс в карантине не считается вторым работающим экземпляром: Stopped/ingress/copies подтверждают изоляцию прежнего экземпляра этой identity до его контролируемого старта; browser ingress остаётся закрытым до enable. Sign вычисляет IsolationEvidenceDigest из подтверждения автоматически; `true` разрешено записывать только после фактической проверки специалистом. VerifiedAtUtc не старше 15 минут. Нельзя заменить проверку набором примерных digest из документа.

Для Join/Restart нет ProtectedBootstrapCommand/BootstrapAccount. Использовать проверенные checkpoint Operations и Nodes (поле Nodes перенести в MonitoringNodes), точные SnapshotId/digest/cut/version. Для первого Join local node уже принят в snapshot как nonvoter. OldBackup=true обязателен для своей восстановленной старой копии. Для аварийного Bootstrap — только prepared snapshot, OldBackup=true, ненулевой MinimumCommittedIndex, RecoveryId manifest; новый account запрещён. Runtime сверяет настоящий локальный cert/private key/CA/SAN/pin и manifest, не доверяет одному файлу.

## Административный HTTP

`GET /api/admin/cluster/lifecycle` возвращает safe registry: nodes, membership changes, ревизии, BotId и trust IDs/fingerprints/сроки/отзыв. PublicCertificate, приватные данные и payload не выдаются. `POST` по тому же адресу требует accepted Administrator и antiforgery; тело camelCase:

```json
{
  "operationId": "cccccccc-cccc-4ccc-8ccc-cccccccccccc",
  "action": "RequestMembership",
  "nodeId": "dddddddd-dddd-4ddd-8ddd-dddddddddddd",
  "baseRevision": 12,
  "membershipVersion": 2,
  "remove": false
}
```

Actions: AdmitNode (storageGeneration/address/credentialId/publicCertificate), RequestMembership (remove), AuthorizeMembership (membershipChangeId), TrustCredential (storageGeneration/credentialId/publicCertificate, при необходимости address), RevokeCredential (credentialId), EnterMaintenance (maintenanceId). BaseRevision — OperationsRevision, MembershipVersion — актуальная native version. Cancel задаёт targetOperationId; Replace дополнительно replacementOperationId/replacementAction и поля нового значения. Для held Replace требуется confirmRestored=true и актуальная base. Original OperationId/payload остаются неизменными при retry. В UI Node Pending показывается безопасно, доступен запрос Cancel; подготовка новой эксплуатационной заявки/held Replace выполняется через этот типизированный API.

Internal ConfirmMembership/ActivateNode/LeaveMaintenance/OpenSenderEpoch через browser или peer batch **запрещены**. Только локальный lifecycle worker с настоящим recovered leader/term/membership provenance принимает их через существующий dispatcher. Любой неизвестный proof оставляет gate закрытым.

## Аварийный запрос

`recovery-request.json` — объект с полями Target (полный ClusterOptions новой identity), Node (OperationalNode нового singleton, Activated=false, Removed=false, Revision=0, настоящий RaftMemberId), Credential (OperationalCredential с реальным публичным cert/pin, AdmissionId/AdmissionExpiresAtUtc=null) и Selection:

| Поле Selection | Значение |
| --- | --- |
| RecoveryId / AdministratorId | новые ID процедуры / действующий общий аккаунт выбранного среза |
| PreviousClusterId / PreviousGeneration | identity исходного snapshot |
| SnapshotPath / SnapshotDigest | абсолютный путь и exact SHA256 проверенного файла |
| SourceIndex / SourceTerm | точный подтверждённый cut выбранного файла |
| KnownCommittedIndex | наибольший известный commit, не меньше SourceIndex |
| LossAcknowledgement | точная английская фраза из runbook, дополнительно вводится интерактивно |
| Isolation | реальная ручная проверка **всех** прежних instances из среза, PreviousBotTokenRevoked=true, PreviousPeerTrustRevoked=true, прежний BotId |

Target.Generation больше прежней; ClusterId/NodeId/storage/credential новые. Target.StoragePath пустой, Mode Bootstrap. Password не является полем запроса. Recover запрашивает пароль активного Administrator выбранного среза; секрет не пишется в файл/аудит. Подтверждение специалиста архивируется во внешнем защищённом журнале вместе с digest пакета; перед выдачей подписанного admission специалист повторно проверяет изоляцию и подписывает свежий challenge. Recover не заменяет эту подпись и не включает работу.
