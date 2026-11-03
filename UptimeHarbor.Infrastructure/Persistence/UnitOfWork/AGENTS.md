# Persistence unit of work

LocalSetupUnitOfWork и OnboardingProgressUnitOfWork — отдельные сценарные scopes на существующей сессии. Revoke setup/hash/epoch/profile атомарны. RuntimeGeneration повторно проверяется перед transaction commit; сеть/файлы/runtime effect выполнять за пределами SQL transaction.

- ClusterLocalInitializationUnitOfWork создаёт только точную LocalNodeState в пустой общей проекции; чужая identity/cursor/shared rows запрещают join без merge. Аккаунты/роль создаёт принятая история. Genesis validation не создаёт JournalPosition с индексом 0.

- Snapshot recovery дополнительно сверяет monitoring image с конфигурацией/observations/incidents/intents/summaries и настоящими accepted delivery attempts, а admitted NodeId/storage generation — с ClusterMember. Cursor/CanConnect не заменяют эту проверку; provider semantics остаются отдельной проверкой.

## Ответственность

Каталог реализует общую scoped-сессию контекста, техническое сохранение/транзакцию и тонкие сценарные Unit of Work.

## Правила

- Не добавлять глобальный Unit of Work.
- Сценарный класс раскрывает только репозитории соответствующего Application-интерфейса.
- ObjectAdministrationUnitOfWork раскрывает совместимые типовые репозитории единого MonitoredServer. Связь, версии и явное удаление notification bindings удаляемой цели сохраняются одним commit; bindings отвязываемых приложений сохраняются.
- `ObjectAdministrationUnitOfWork` условным update отменяет recovery jobs прежнего incident в той же транзакции, что disable/endpoint change; job не удаляется, причина хранится в payload.
- `MonitoringUnitOfWork` используется для атомарной записи результата одного объекта и outage/recovery outbox job; параллельные проверки не разделяют его DI scope.
- Технический Unit of Work преобразует EF concurrency exception в `PersistenceConcurrencyException` и очищает stale tracking.
- SQL Server 2601/2627 преобразовывать в `PersistenceConflictException` только для известных Telegram entities; прочие `DbUpdateException` пробрасывать.
- `NotificationSettingsUnitOfWork` фиксирует результат одной привязки в отдельном delivery scope, чтобы частичный успех получателей не объединялся.
- Транзакция выполняет действие, единое сохранение и commit; при ошибке выполняется rollback и сохраняется исходное исключение.
- Не создавать новый `DbContext` внутри репозитория или Unit of Work.
- При изменении транзакционной семантики обновлять тесты, этот файл и `Documentation/Technical documentation/Persistence.md`.
- `ProjectionApplyUnitOfWork` проверяет cursor/receipt и атомарно сохраняет решённый пакет проекции; одинаковый повтор возвращает false, несовпадающая позиция/digest отвергается. После rollback очищается tracker; принятый префикс поступает через DotNext и production reducer.
- `SnapshotProjectionUnitOfWork` проверяет образ до SQL install, заменяет только whitelist общих полей и cursor/checkpoint одной внешней транзакцией. Два SaveChanges не являются двумя commit; локальные Identity counters, SessionEpoch, Pending и ключи сохраняются. SQL semantics требуют отдельной provider-проверки.
- Recovery сверяет SQL account set, login/hash/format/stamps/version/deletion и единственную role/membership с принятым образом; одного cursor или CanConnect недостаточно. Genesis требует отсутствия самостоятельных аккаунтов/ролей.
- `PendingControlUnitOfWork`, `ObservationCaptureUnitOfWork`, `DeliveryResultUnitOfWork` разделяют локальные сценарии; для sequence и capture использовать одну транзакцию. Они не объявляют локальные результаты принятыми всей сеткой.
