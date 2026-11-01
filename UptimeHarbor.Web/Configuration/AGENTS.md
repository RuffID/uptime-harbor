# Web configuration

## Ответственность

Каталог содержит привязку типизированных options и их fail-fast валидацию. Serilog настраивается в composition root через секцию `Serilog` из `appsettings.json`; JSON-файлы operational-конфигурации находятся в соседнем каталоге `Config`. `TelegramOptions` принадлежит Infrastructure, `MonitoringOptions` — Application, Identity options — Infrastructure.

## Правила

- Каждой группе настроек соответствует отдельный options-тип и константа имени секции.
- `ClusterOptions` принадлежит Infrastructure/Cluster; его binding/валидация находятся в AddClusterInfrastructure, listener validation — Web/Cluster. Безопасный пример оставлять Disabled; secret config и запуск не изменять автоматически ради включения.
- Обязательные значения валидировать при старте без fallback-значений, скрывающих ошибку.
- Не передавать `IConfiguration` прикладным сервисам и контроллерам.
- Не логировать пароли, токены и строку подключения.
- При изменении operational/security settings обновлять `Config/config.example.json`, локальный `Config/config.json`, тесты, этот файл и `Documentation/Technical documentation/Configuration.md`; `appsettings.json` изменять только для Serilog.
- `Config/config.json` обязателен при старте, не отслеживается и не копируется в build/publish; `Config/config.example.json` содержит только безопасные placeholders и никогда не загружается приложением.
- Serilog использует один человекочитаемый шаблон с UTC-временем, уровнем, source context и structured properties для console и File sink `Logs/log_*.log`; retention, size limit и непубличное расположение File sink обязательны.
- Изменения sinks синхронизировать с Docker log volume, `.gitignore`, publish exclusion, тестами и документацией; секреты не логировать.
- Общий для worker и HTTP-адаптера тип `MonitoringOptions` размещать в Application, но привязывать и валидировать только здесь.
- `TelegramOptions` размещать рядом с Infrastructure adapter, но привязывать и валидировать только Web composition root.
- При старте отклонять пустой, некорректный и примерный `Telegram:BotToken` через `HasValidBotToken`, не раскрывая значение. Это проверка формата; действительность токена проверяется Telegram при отправке.
