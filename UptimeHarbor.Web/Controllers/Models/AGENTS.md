# HTTP request-модели

Отдельные request-типы описывают настройки объектов, пользователей, видимость и Telegram bindings.

Настройки контейнера передают nullable `ServerId` (null снимает связь), настройки сервера — optional `ContainerLinks` с версиями и выбранностью контейнеров. Старый вложенный POST и новый общий POST контейнеров используют одну Application-команду.

Сохранять контракты JSON и validation, `Version` для stale updates и подтверждённое количество контейнеров при удалении сервера. Token Telegram и persistence entities в запросы UI не добавлять.
