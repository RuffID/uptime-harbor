# UptimeHarbor.Infrastructure

## Ответственность

Проект реализует persistence, Identity и внешние адаптеры. Он зависит от Application и Domain; `EFCoreLibrary` и `HttpClientLibrary` используются только здесь.

## Правила

- Реализовывать чистые Application-контракты, не протаскивая инфраструктурные типы наружу.
- Все EF-запросы, tracking и materialization держать внутри Infrastructure.
- Регистрировать зависимости модулем `AddInfrastructure`, а не напрямую в `Program.cs`.
- Типизированные настройки получать через options; `IConfiguration` использовать только при регистрации.
- При изменении интеграций, Identity или persistence обновлять этот файл и тематическую документацию `Documentation/Technical`.
- Identity bootstrap и user store остаются в `Identity`; начальный пароль используется только при создании отсутствующего администратора.
- Блокировка пользователя обязана обновлять security stamp, а cookie validator — проверять `IsEnabled` на каждом запросе.
