# UptimeHarbor.Infrastructure

## Ответственность

Проект реализует persistence, Identity и внешние адаптеры. Он зависит от Application и Domain; `EFCoreLibrary` и `HttpClientLibrary` используются только здесь.

## Правила

- Реализовывать чистые Application-контракты, не протаскивая инфраструктурные типы наружу.
- Все EF-запросы, tracking и materialization держать внутри Infrastructure.
- Регистрировать зависимости модулем `AddInfrastructure`, а не напрямую в `Program.cs`.
- Для production persistence использовать провайдер EF Core SQL Server и строку подключения `ConnectionStrings:SqlServer`.
- Типизированные настройки получать через options; `IConfiguration` использовать только при регистрации.
- При изменении интеграций, Identity или persistence обновлять этот файл и тематическую документацию `Documentation/Technical`.
- Identity bootstrap и user store остаются в `Identity`; начальный пароль используется только при создании отсутствующего администратора.
- Блокировка пользователя обязана обновлять security stamp, а cookie validator — проверять `IsEnabled` на каждом запросе.
- Неудачные входы учитываются Identity lockout: пять ошибок дают временную блокировку на пять минут; `IsEnabled` и Identity lockout остаются разными состояниями.
- Специализированные dashboard и recipient read queries materialize-ят Application read records; список пользователей для SignalR загружается одним query с признаком роли Administrator, permissions — пакетно, без per-user `IsInRoleAsync`.
- HTTP-проверки мониторинга и hosted worker находятся в `Monitoring`; adapter использует `IHttpClientFactory`, а параллельные объекты обрабатываются в отдельных scopes.
- Telegram transport, durable outbox dispatcher и worker находятся в `Telegram`; token получается только через `TelegramOptions`, стандартное HTTP-логирование клиента отключено, а ошибки чатов изолируются.
- Telegram dispatcher и worker читают durable outbox независимо от monitoring worker; retry ограничен, а transport-семантика документирована как at-least-once.
