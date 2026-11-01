# Доменные настройки уведомлений

`TelegramChat` хранит настройки получателя, `NotificationBinding` — ровно одну цель, состояние outage/recovery доставки и привязку к инциденту.

Сохранять инвариант единственного target и per-binding markers. Отправка, retry, lease и durable outbox принадлежат Infrastructure; Telegram token не входит в эти сущности.

`ConfigRevision` чата и `BindingRevision`/`BindingGeneration` привязки относятся к принятой конфигурации. Общая история intents/attempts, сводки и recovery entitlements находятся в `Cluster`; существующие per-binding markers не заменяют эту историю.
