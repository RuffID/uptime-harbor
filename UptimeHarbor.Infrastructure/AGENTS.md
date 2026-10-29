# UptimeHarbor.Infrastructure

- Распределённый monitoring capture/reducer integration описан в Monitoring/AGENTS.md и Cluster/AGENTS.md; отдельного consensus, SQL WAL и dispatcher доставки не создавать. Наличие зарегистрированных компонентов не открывает activation/worker/sender gates.

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
- Identity bootstrap и user store остаются в `Identity`. Disabled сохраняет legacy bootstrap; enabled использует принятый sign-in/credentials и запрещает локальные account writers. Password validators/hasher готовят credential до durable Pending; локальный login UoW меняет только counters/lockout. Детальные границы session invalidation и точного bootstrap proof — в `Identity/AGENTS.md`.
- При старте `DataBaseCheckUpService<UptimeHarborDbContext>` проверяет SQL Server, создаёт backup при наличии pending migrations и применяет их до Identity bootstrap; ошибка подключения, backup или migration останавливает запуск.
- Backup и migration не должны логировать connection string, credentials или SQL command; имена pending migrations логировать разрешено.
- Блокировка пользователя обязана обновлять security stamp, а cookie validator — проверять `IsEnabled` на каждом запросе.
- Неудачные входы учитываются Identity lockout: пять ошибок дают временную блокировку на пять минут; `IsEnabled` и Identity lockout остаются разными состояниями.
- Специализированные dashboard и recipient read queries materialize-ят Application read records; список пользователей для SignalR загружается одним query с признаком роли Administrator, без per-user `IsInRoleAsync`. Таблиц ресурсных permissions в целевой модели нет.
- HTTP-проверки мониторинга и hosted worker находятся в `Monitoring`; adapter использует `IHttpClientFactory`, а параллельные объекты обрабатываются в отдельных scopes.
- Telegram transport, durable outbox dispatcher и worker находятся в `Telegram`; token получается только через `TelegramOptions`, стандартное HTTP-логирование клиента отключено, а ошибки чатов изолируются.
- Telegram dispatcher и worker читают durable outbox независимо от monitoring worker; retry ограничен, а transport-семантика документирована как at-least-once.
- Dispatcher сериализует pre-send gate с terminal recovery cancellation; SQL Server unique errors имени сервера и Telegram классифицируются в Infrastructure без утечки provider типов в Application.
- Общее хранилище сетки — восстанавливаемая SQL-проекция Raft log + snapshot. `Cluster` хранит term/vote/log и membership через DotNext на отдельном provisioned диске и подключает production reducer/replay через `RevisionClusterRecovery`. Локальные секреты и lockout не входят в общие данные; admission/trust будущего lifecycle по умолчанию закрыты.
- В enabled-режиме не регистрировать прежние monitoring/outbox workers, не выполнять legacy startup migration/bootstrap и не пропускать прежний sender в обход authority/attempt. Disabled сохраняет однопроцессную модель. Подробности — `Documentation/Technical documentation/Cluster runtime.md`.
- Accepted delivery dispatcher/consensus/test-submit находятся в Cluster, HTTP transport — в Telegram. Регистрировать scoped transport/result UoW отдельно от singleton dispatcher; production sender graph собирается в AddClusterWeb и не открывает отсутствующие readiness proof.
