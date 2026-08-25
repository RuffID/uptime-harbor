# Конфигурация и запуск

## Источники настроек

Web-проект использует стандартную конфигурацию ASP.NET Core. В репозитории находятся `appsettings.json` и `appsettings.Development.json`; production-секреты должны поступать из внешнего файла, который не включается в image и репозиторий.

## Типизированные группы

Определены три options-типа:

- `BootstrapAdminOptions` — логин и начальный пароль администратора;
- `MonitoringOptions` — принадлежащий Application общий контракт интервала, timeout и порога ошибок;
- `TelegramOptions` — токен бота.

`MonitoringOptions` и `TelegramOptions` привязываются в Web через `AddApplicationConfiguration` и используют `ValidateOnStart`. Monitoring worker и HTTP-адаптер получают только `IOptions<MonitoringOptions>` и не зависят от `IConfiguration`. `BootstrapAdminOptions` принадлежит Infrastructure и валидируется при bootstrap только если администратор отсутствует. Это позволяет повторному запуску с уже созданным администратором не зависеть от сохранения начального пароля в конфигурации.

Строка подключения `ConnectionStrings:PostgreSql` проверяется в `AddInfrastructure` до регистрации DbContext.

## DI validation

В Development включены `ValidateScopes` и `ValidateOnBuild`. Это обнаруживает captive dependencies и недоступные регистрации при построении контейнера.

## Логирование

`AddUptimeHarborLogging` очищает стандартные providers и включает JSON console logger с UTC timestamp и scopes. Пароли, токены, строка подключения и cookies не должны передаваться в сообщения или scopes.

## Время

Application регистрирует `TimeProvider.System`. Сценарии, зависящие от текущего времени, должны получать `TimeProvider` через DI, а не обращаться напрямую к системным часам.

## Текущее ограничение

Bootstrap администратора и monitoring hosted service реализованы. Telegram sender пока не реализован, поэтому `TelegramOptions` валидируются, но ещё не имеют production-потребителя.
