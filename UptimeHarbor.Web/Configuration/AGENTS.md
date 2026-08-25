# Web configuration

## Ответственность

Каталог содержит привязку типизированных options, их fail-fast валидацию и настройку структурированного логирования. `TelegramOptions` принадлежит Infrastructure, `MonitoringOptions` — Application, Identity options — Infrastructure.

## Правила

- Каждой группе настроек соответствует отдельный options-тип и константа имени секции.
- Обязательные значения валидировать при старте без fallback-значений, скрывающих ошибку.
- Не передавать `IConfiguration` прикладным сервисам и контроллерам.
- Не логировать пароли, токены и строку подключения.
- При изменении настроек обновлять appsettings, тесты, этот файл и `Documentation/Technical/Configuration.md`.
- Общий для worker и HTTP-адаптера тип `MonitoringOptions` размещать в Application, но привязывать и валидировать только здесь.
- `TelegramOptions` размещать рядом с Infrastructure adapter, но привязывать и валидировать только Web composition root.
