# UptimeHarbor.Infrastructure

## Ответственность

Проект реализует persistence, Identity и внешние адаптеры. Он зависит от Application и Domain; `EFCoreLibrary` и `HttpClientLibrary` используются только здесь.

Локальная поставка DLL рассчитана на `net10.0` и зависимости `10.0.12`; состав и обновление описаны в `Documentation/Technical documentation/Libraries.md`. EF adapter регистрируется через `AddEfCoreContext<UptimeHarborDbContext, UptimeHarborDbContext>()`; репозитории используют `IContext*Repository` с тем же ключом контекста.

## Правила

- Реализовывать чистые Application-контракты, не протаскивая инфраструктурные типы наружу.
- Все EF-запросы, tracking и materialization держать внутри Infrastructure.
- Регистрировать зависимости модулем `AddInfrastructure`, а не напрямую в `Program.cs`.
- Для production persistence использовать провайдер EF Core SQL Server и строку подключения `ConnectionStrings:SqlServer`.
- Типизированные настройки получать через options; `IConfiguration` использовать только при регистрации.
- При изменении интеграций, Identity или persistence обновлять этот файл и тематическую документацию `Documentation/Technical documentation`.
- Identity bootstrap и user store остаются в `Identity`; начальный пароль используется только при создании отсутствующего администратора.
- При старте `DataBaseCheckUpService<UptimeHarborDbContext>` проверяет SQL Server, создаёт backup при наличии pending migrations и применяет их до Identity bootstrap; ошибка подключения, backup или migration останавливает запуск.
- Backup и migration не должны логировать connection string, credentials или SQL command; имена pending migrations логировать разрешено.
- Блокировка пользователя обязана обновлять security stamp, а cookie validator — проверять `IsEnabled` на каждом запросе.
- Неудачные входы учитываются Identity lockout: пять ошибок дают временную блокировку на пять минут; `IsEnabled` и Identity lockout остаются разными состояниями.
- Специализированные dashboard и recipient read queries materialize-ят Application read records; список пользователей для SignalR загружается одним query с признаком роли Administrator, без per-user `IsInRoleAsync`. Таблиц ресурсных permissions в целевой модели нет.
- HTTP-проверки мониторинга и hosted worker находятся в `Monitoring`; adapter использует `IHttpClientFactory`, а параллельные объекты обрабатываются в отдельных scopes.
- Telegram transport, durable outbox dispatcher и worker находятся в `Telegram`; token получается только через `TelegramOptions`, стандартное HTTP-логирование клиента отключено, а ошибки чатов изолируются.
- Telegram dispatcher и worker читают durable outbox независимо от monitoring worker; retry ограничен, а transport-семантика документирована как at-least-once.
- Dispatcher сериализует pre-send gate с terminal recovery cancellation; SQL Server unique errors имени сервера и Telegram классифицируются в Infrastructure без утечки provider типов в Application.
- Общее хранилище сетки — восстанавливаемая SQL-проекция будущего Raft log + snapshot. Storage-регистрации не включают consensus/reducer/replication workers; локальные секреты и lockout не входят в общие данные.
