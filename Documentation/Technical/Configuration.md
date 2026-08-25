# Конфигурация и запуск

## Источники настроек

Web-проект использует стандартную конфигурацию ASP.NET Core. В репозитории находятся `appsettings.json` и `appsettings.Development.json`; production-секреты должны поступать из внешнего файла, который не включается в image и репозиторий.

## Типизированные группы

Сейчас определены три options-типа:

- `BootstrapAdminOptions` — логин и начальный пароль администратора;
- `MonitoringOptions` — интервал, timeout и порог ошибок;
- `TelegramOptions` — токен бота.

`AddApplicationConfiguration` привязывает секции и включает `ValidateOnStart`. Пустые обязательные строки и неположительные числовые значения приводят к понятной ошибке запуска. Запасные значения, скрывающие некорректную конфигурацию, не используются.

Строка подключения `ConnectionStrings:PostgreSql` проверяется в `AddInfrastructure` до регистрации DbContext.

## DI validation

В Development включены `ValidateScopes` и `ValidateOnBuild`. Это обнаруживает captive dependencies и недоступные регистрации при построении контейнера.

## Логирование

`AddUptimeHarborLogging` очищает стандартные providers и включает JSON console logger с UTC timestamp и scopes. Пароли, токены, строка подключения и cookies не должны передаваться в сообщения или scopes.

## Время

Application регистрирует `TimeProvider.System`. Сценарии, зависящие от текущего времени, должны получать `TimeProvider` через DI, а не обращаться напрямую к системным часам.

## Текущее ограничение

Bootstrap администратора, monitoring hosted service и Telegram sender пока не реализованы, поэтому их options уже валидируются, но ещё не имеют production-потребителей.
