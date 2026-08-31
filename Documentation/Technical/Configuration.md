# Конфигурация и запуск

## Источники настроек

После `WebApplication.CreateBuilder` Web-проект использует стандартные источники ASP.NET Core, а затем добавляет обязательный `Config/config.json` относительно content root с `optional: false` и `reloadOnChange: false`. Поэтому этот файл имеет приоритет над ранее добавленными стандартными источниками и должен существовать до старта. Отсутствующий файл или некорректный JSON останавливают запуск до регистрации прикладных и инфраструктурных сервисов.

Назначение файлов разделено:

- `appsettings.json` содержит только секцию `Serilog`;
- `appsettings.Development.json` содержит только нечувствительные development-only настройки, сейчас `DetailedErrors`;
- `Config/config.json` содержит operational/security settings и секреты, не отслеживается git и не копируется в build output или publish;
- `Config/config.example.json` — отслеживаемый полный безопасный пример с placeholders, который приложение никогда не загружает.

Для локального или опубликованного запуска нужно скопировать `Config/config.example.json` в `Config/config.json`, затем заменить bootstrap login/password, пароль SQL Server, Telegram bot token и при необходимости proxy CIDR/`AllowedHosts`. При publish файл не упаковывается: окружение запуска должно предоставить его снаружи по пути `Config/config.json` относительно content root.

В `Config/config.json` находятся секции `BootstrapAdmin`, `ConnectionStrings:SqlServer`, `Monitoring`, `Telegram`, `ForwardedHeaders` и `AllowedHosts`. В репозитории нет настоящих значений секретов.

## Типизированные группы

Определены три options-типа:

- `BootstrapAdminOptions` — логин и начальный пароль администратора;
- `MonitoringOptions` — принадлежащий Application общий контракт интервала, timeout и порога ошибок;
- `TelegramOptions` — принадлежащий Infrastructure тип с токеном бота.

`MonitoringOptions` и `TelegramOptions` привязываются в Web через `AddApplicationConfiguration` и используют `ValidateOnStart`. Monitoring worker и HTTP-адаптер получают только `IOptions<MonitoringOptions>`, Telegram adapter — только `IOptions<TelegramOptions>`; потребители не зависят от `IConfiguration`. `BootstrapAdminOptions` принадлежит Infrastructure и валидируется при bootstrap только если администратор отсутствует. Это позволяет повторному запуску с уже созданным администратором не зависеть от сохранения начального пароля в конфигурации.

Строка подключения `ConnectionStrings:SqlServer` проверяется в `AddInfrastructure` до регистрации DbContext. Она поступает из обязательного внешнего `Config/config.json`; секреты не хранятся в репозитории или publish-артефакте.

`ForwardedHeaders:KnownNetworks` задаёт CIDR доверенной proxy-сети; приложение принимает forwarded-заголовки только из этой сети и ограничивает цепочку одним прокси (`ForwardLimit = 1`). Значение сети должно совпадать с подсетью используемого reverse proxy.

## DI validation

В Development включены `ValidateScopes` и `ValidateOnBuild`. Это обнаруживает captive dependencies и недоступные регистрации при построении контейнера.

## Логирование

Web использует настоящий Serilog через `builder.Host.UseSerilog` и `ReadFrom.Configuration(builder.Configuration)`. Секция `Serilog` задаёт уровень `Information` по умолчанию, warning overrides платформы, `FromLogContext` для scopes и два sinks с единым человекочитаемым UTC-шаблоном:

- Console выводит строки вида `[UTC-время] [уровень][source context] - сообщение {structured properties}`;
- File пишет человекочитаемые строки вида `[UTC-время] [уровень][source context] - сообщение {structured properties}` в `Logs/log_*.log`, переключает файл ежедневно и при достижении 10 MiB, хранит не более 31 файла. Исключение выводится следующей строкой со stack trace.

`Logs` расположен вне `wwwroot`, игнорируется git и исключён через `DefaultItemExcludes` из build/publish. При локальном запуске каталог создаёт File sink с правами процесса. В container `/app/Logs` имеет режим `0700` и владельца non-root app user; Docker volume сохраняет файлы между пересозданиями контейнера. При bind mount оператор обязан настроить совместимые owner/permissions на host.

Connection string, пароли, Telegram token, cookies, request/response body и полный Telegram URI запрещено передавать в сообщения, properties и scopes: File sink получает те же события, что и Console, и не является дополнительным фильтром. Serilog request-logging middleware не включён. Стандартное HTTP-логирование Telegram-клиента по-прежнему удалено, чтобы token из URI не попадал в логи.

## Время

Application регистрирует `TimeProvider.System`. Сценарии, зависящие от текущего времени, должны получать `TimeProvider` через DI, а не обращаться напрямую к системным часам.

## Telegram-секрет

Используется один bot token. Он не передаётся в HTML, TypeScript или административный API. Telegram Bot API включает token в URL, поэтому стандартное HTTP-логирование этого клиента отключено; adapter не включает URI, тело ответа или текст сообщения в исключения.
