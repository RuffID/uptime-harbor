# EF configurations

## Ответственность

Каталог задаёт таблицы, ключи, ограничения, индексы, связи и правила удаления для доменной и Identity-модели.

## Правила

- Каждую сущность настраивать отдельным `IEntityTypeConfiguration<T>`.
- Дублировать критичные инварианты ограничениями БД, когда они должны действовать при любом способе записи.
- Не менять имена таблиц, ключей и constraints без оценки миграции и совместимости.
- SQL в check constraints и filtered indexes писать в синтаксисе Microsoft SQL Server.
- При изменении конфигураций обновлять model tests, этот файл и `Documentation/Technical documentation/Persistence.md`.
- Состояние частичной доставки хранить на `NotificationBinding` по incident ID; не возвращать глобальный флаг объекту мониторинга.
- Unique indexes Telegram chat ID и обеих chat/target пар имеют стабильные явные имена, используемые при сопровождении SQL Server conflict classification.
- `MonitoredContainer.ServerId` nullable; server FK сохраняет `NoAction`. Container permission ссылается на контейнер по его ID, а соответствие server scope проверяется Application и read queries; nullable поле не включать в alternate key.
- `MonitoredServer.Version` и `MonitoredContainer.Version` настраивать как optimistic concurrency token.
- `MonitoredServer.Name` настраивать как обязательное поле с уникальным индексом `ux_monitored_servers_name`.
- Outbox job не связывать внешним ключом с удаляемым объектом мониторинга: сохранённый payload должен переживать административное удаление.
