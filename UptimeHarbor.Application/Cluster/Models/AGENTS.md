# Cluster contracts

- MonitoringImage является частью того же accepted snapshot: raw observations, отдельная acceptance provenance, streams, live/history, incidents/events/intents/summaries и реальные delivery evidence. Metadata остаются по observer; diagnostic MonitoringProgress не содержит payload.
- MonitoringNodes приходит из trusted recovery admission, не из пользовательского payload; пустой/несверенный реестр не даёт внутреннюю авторизацию. Наличие модели delivery evidence не означает реализованный producer attempts.

Диагностические записи содержат безопасные статусы/ID/подтверждения и backlog без credentials/payload. Внутренние command/image/snapshot модели могут содержать защищённые данные аккаунта; не выдавать их в UI, health, ToString или логи.
