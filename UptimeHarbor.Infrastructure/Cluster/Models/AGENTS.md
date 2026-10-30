# Peer и disk models

ClusterControlRequest/SignedClusterControl связывают подписанный локальный grant с процессом и версиями. ManualIsolationConfirmation удостоверяет фактическую ручную проверку специалиста, не timeout; внешние записи содержат безопасные digest/IDs, секреты запрещены. EmergencyRecoverySelection не содержит пароль, output остаётся quarantined.

Форматы envelope/manifest меняются только вместе с compatibility/recovery контрактом. Не добавлять приватные ключи, token или бизнес-payload в диагностические DTO; manifest не является общим SQL state.
