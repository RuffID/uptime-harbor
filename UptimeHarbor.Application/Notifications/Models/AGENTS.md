# Модели уведомлений

DTO чатов, привязок, целей и ожидающих доставок связывают административный и delivery-сценарии. `NotificationDeliveryKind` различает outage и recovery.

Не передавать token, полный Telegram URI, lease или EF outbox entity в UI-модели. Семантика доставки относится к конкретным binding и incident.
