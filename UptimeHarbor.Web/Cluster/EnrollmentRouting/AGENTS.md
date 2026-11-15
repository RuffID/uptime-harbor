# Peer enrollment routing

Контроллер принимает только verified PeerEnvelope из PeerBoundaryMiddleware на прежнем isolated mTLS listener. Nonvoter, browser, pending probe и общий commands batch этих прав не получают. Ограничения: fixed routes, strict JSON, 64 KiB, 10 секунд, replay и accepted voting origin/storage.

EnrollmentPeerClient использует только named PeerClientHandler и accepted address назначенного issuer/текущего native leader. Не возвращать чужой browser descriptor, не копировать peer DTO в projection. После RPC повторять native linearizable read и term/leader/membership; coordinate success подтверждать локальным accepted result с исходным digest.

Remote grant требует отдельный purpose-bound peer-key ProcessProof целевого voter, а signer повторяет accepted template/process/issuer checks под своим lock. Подпись не принимает membership/activation/Enable и не разрешает CatchUp. Local process proof Bootstrap/ManagedRestart сохраняется; последующие lifecycle exchanges принадлежат 04.12.
