# Конфигурация и запуск

## Источники настроек

Web-проект использует стандартную конфигурацию ASP.NET Core. В репозитории находятся `appsettings.json` и `appsettings.Development.json`; production-секреты должны поступать из внешнего файла, который не включается в image и репозиторий.

## Типизированные группы

Определены три options-типа:

- `BootstrapAdminOptions` — логин и начальный пароль администратора;
- `MonitoringOptions` — принадлежащий Application общий контракт интервала, timeout и порога ошибок;
- `TelegramOptions` — принадлежащий Infrastructure тип с токеном бота.

`MonitoringOptions` и `TelegramOptions` привязываются в Web через `AddApplicationConfiguration` и используют `ValidateOnStart`. Monitoring worker и HTTP-адаптер получают только `IOptions<MonitoringOptions>`, Telegram adapter — только `IOptions<TelegramOptions>`; потребители не зависят от `IConfiguration`. `BootstrapAdminOptions` принадлежит Infrastructure и валидируется при bootstrap только если администратор отсутствует. Это позволяет повторному запуску с уже созданным администратором не зависеть от сохранения начального пароля в конфигурации.

Строка подключения `ConnectionStrings:SqlServer` проверяется в `AddInfrastructure` до регистрации DbContext. В production она передаётся внешней конфигурацией, например через переменную окружения `ConnectionStrings__SqlServer`; секреты не хранятся в репозитории.

`ForwardedHeaders:KnownNetworks` задаёт CIDR доверенной proxy-сети; приложение принимает forwarded-заголовки только из этой сети и ограничивает цепочку одним прокси (`ForwardLimit = 1`). Значение сети должно совпадать с подсетью используемого reverse proxy.

## DI validation

В Development включены `ValidateScopes` и `ValidateOnBuild`. Это обнаруживает captive dependencies и недоступные регистрации при построении контейнера.

## Логирование

`AddUptimeHarborLogging` очищает стандартные providers и включает JSON console logger с UTC timestamp и scopes. Пароли, токены, строка подключения и cookies не должны передаваться в сообщения или scopes.

## Время

Application регистрирует `TimeProvider.System`. Сценарии, зависящие от текущего времени, должны получать `TimeProvider` через DI, а не обращаться напрямую к системным часам.

## Telegram-секрет

Используется один bot token. Он не передаётся в HTML, TypeScript или административный API. Telegram Bot API включает token в URL, поэтому стандартное HTTP-логирование этого клиента отключено; adapter не включает URI, тело ответа или текст сообщения в исключения.
