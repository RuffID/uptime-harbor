# Доменные настройки уведомлений

`TelegramChat` хранит настройки получателя, `NotificationBinding` — ровно одну цель, состояние outage/recovery доставки и привязку к инциденту.

Сохранять инвариант единственного target и per-binding markers. Отправка, retry, lease и durable outbox принадлежат Infrastructure; Telegram token не входит в эти сущности.
