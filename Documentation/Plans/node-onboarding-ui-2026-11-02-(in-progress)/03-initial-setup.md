# 03. Первоначальная настройка

Статус: `completed` (2026-11-03, подготовительный объём). Зависимости: [01](01-scenarios-and-decisions.md), [02](02-invitations-and-contracts.md).

## Цель

Дать локальному администратору защищённый setup и реализовать переключение новой установки в кластерный режим без перезапуска Web-приложения и без несвязанных рабочих данных.

## Работы

1. Ввести явный setup-профиль до legacy bootstrap и до запуска рабочих Cluster workers. Выбор «Создать новый кластер» / «Подключиться» не выводится из отсутствия объектов или первой HTTP-сессии.
2. Реализовать вход локальным администратором, изолированные setup routes/assets и отзыв setup-сессий после готовности. Безопасную подготовку локального аккаунта отделить от общего bootstrap; не разрешать публичное создание администратора по первому запросу. Не требовать дополнительного установочного секрета. Сохранять общую авторизацию, CSRF/origin policy, rate limit и peer/browser разделение; не открывать весь API исключением из middleware.
3. Реализовать минимальный preflight: схема, shared-таблицы/identity/cursor, storage/manifest, версии, постоянные тома и доступные адреса. Единственная изолированная setup-учётная запись не блокирует Join; рабочие данные, посторонние аккаунты и старый cluster storage вызывают отказ без очистки или merge. Действующий первый standalone направляется в этапы 03а/03б.
4. Хранить локальный профиль режима, references на identity/keys и AttemptId в локальной БД вне общей репликации. Приватные ключи хранить защищённо локально, файлы создавать атомарно с ограниченными путями/ACL. Проверять согласованность SQL/storage после обрыва; не переписывать config и секреты оператора.
5. Для новой первой установки подготовить координацию BootstrapAccount/начального членства через типизированные authority-порты; здесь проверить lifecycle с doubles при закрытых production gates. Реальные issuer/grants и интегрированную готовность завершает 04. Не устанавливать Ready/Activated вручную; общий вход и приглашения открываются только после связки 03+04.
6. Для нового второго узла сохранить локальные NodeId/storage/AttemptId/private key, не создавать независимого общего администратора или второй Bootstrap. Перезапуск восстанавливает ту же попытку.
7. Реализовать управляемый lifecycle без перезапуска Web-host: остановка standalone workers, короткая пауза операций, подготовка проекции, безопасная загрузка новых credentials и однократный старт cluster workers. Подготовить peer listener для перехода с закрытым доступом до готовности. Простое изменение поля режима не заменяет переключение; shell-команды и supervisor-restart из HTTP не используются.
8. Логировать начало настройки, успешный preflight, безопасную причину отказа и переход профиля после durable результата. Установочный credential, ключи, connection string и пароль нового администратора не логировать.

## Затрагиваемые части

### Контракт, разработанный этапом 01

Реализовать [локальную настройку/runtime](technical-design.md#runtime): явный InstallationIntent до legacy bootstrap, LocalSetupAccount вне users/roles, подготовка из известных локальных admin credentials без публичной регистрации и отдельного setup token. Отдельные cookie/DP purpose/epoch, delete hash + revoke setup перед common routes. Существующий legacy-admin на втором не становится setup account. Для нового первого общий account создаётся отдельно, без слияния setup identity.

Управляемые поколения всех legacy/cluster workers и delegating service selection, atomic profile revision, lock до preflight, write/drain gate, file/SQL reconciliation и dynamic cert/trust providers обязательны. Direct listener/TLS passthrough готовится до Build, endpoint отсутствует — preflight отказ, не скрытый restart. Сохранить локальные profile/secret volumes; DPAPI не вводить. В 03 обеспечить отсутствие зависимости setup/CanWork/consensus-health/resign от Telegram и убрать обязательность token на несендерах из startup validation; реальные sender gates/renewal/dispatch реализует 05а. До неё доставка закрыта, Join/импорт могут работать.

Этап 03 реализует runtime/authority **порты** и проверяет с doubles; реальный genesis/сертификат/online activation завершается совместно с 04. Не требовать результата будущего 04 для завершения разработки 03 и не открывать production gates по doubles. Для существующего первого production cutover только 03б. Проверить first-visitor takeover/config reset/cookie crossing/nonempty local tables/double process/failed drain/stale callbacks/повреждённые refs; события — по [матрице](technical-design.md#verification). При реализации актуализировать устаревшую пометку о будущем общем входе в Application/AGENTS.md по фактическим границам.

Web composition/configuration, boundary middleware, startup registration, Infrastructure initialization/options/control и локальное хранение. Изменения Program.cs остаются вызовами модулей. Синхронизировать местные AGENTS.md и Configuration, Web application, Cluster operations, Deployment после реализации.

## Проверки и приёмка

- Свежий узел допускает setup только после локального административного входа; посторонний, приглашение чужого кластера и cross-site запрос отклоняются. После Join прежняя setup-сессия не действует.
- Изолированная setup-учётная запись допускается; рабочие данные, посторонние legacy-аккаунты, старый manifest и клон identity не запускают новый Join/Bootstrap.
- Нормальные Disabled-установки не меняют поведение без явного выбора поддержанного перехода.
- Перезапуск не теряет AttemptId и не переиспользует challenge предыдущего процесса.
- В production/setup графе DI нет двойного старта workers, преждевременного WAL resolve и обхода closed gates.
- Обычное подключение не меняет процесс Web-host и не требует ручного перезапуска; сбой на каждом runtime-переходе сохраняет проверяемое состояние и прежний AttemptId.
- Runtime нового первого готов к подключению реальной authority этапа 04; интегрированный Bootstrap становится источником приглашения после 03+04 и проверки 07. Действующий standalone проходит 03а/03б до выдачи кода. Doubles не подтверждают production Bootstrap.

## Результат

Подготовительный объём выполнен. Явный Setup до legacy initialization, отдельный LocalSetupAccount/login/cookie, точные CSRF/Origin/Host boundaries, preflight всех mapped shared/local таблиц и queues, локальные profile/attempt/step CAS/UoW, AES-GCM files/inline invitation, installation/storage locks и reconciliation реализованы. Новый первый подготавливает отдельного общего администратора и BotId; второй сохраняет свои IDs/key и не создаёт Bootstrap. Конфигурация установки и содержание завершённых 01/02 не менялись.

Реализованы typed ISetupAuthority/IManagedRuntimeAdmission/IInstallationRuntime, полная машина фаз и supervisor с deferred worker inventory, immutable active profile, write/drain generations, bounded stop и stale callback guards. Заранее подготовленный Direct/TLS-passthrough listener использует динамический certificate/trust provider. Setup/CanWork/consensus health/resign не требуют Telegram, новый delivery path закрыт. Доступен только минимальный login/setup E0 UI, без мастера подключения и списка узлов. События логируются по durable результату без секретов.

Production adapters authority/admission остаются Unavailable: настоящий genesis/issuer/grants/online activation принадлежит 04, полный Join — 05, sender — 05а, экраны — 06. Local save/evidence double не объявляются Bootstrap/Join/Ready. Удаление setup hash/отзыв sessions перед common publication проверены с doubles; production gate не открыт. Действующий standalone не переводился: его непустота направляет в 03а/03б. Это согласованная граница завершения 03, а не требование реализовать будущий 04.

Проверки: Application 266, Infrastructure Installation+Cluster 166, Web 162, frontend 2 — без ошибок/пропусков; отдельные production/test compile-check и tsc успешны, C# warnings/errors отсутствуют. Сгенерирована только разрешённая AddLocalInstallationSetup, к БД не применялась. Покрыты takeover/config reset, cookie/CSRF/origin crossing, непустые mapped/local таблицы, второй exclusive file handle, failed drain/double start/stale callbacks, потеря ответа/исходные IDs/revisions/restart, corrupted refs/orphans, safe logs и отсутствие Telegram зависимости несендера. [Команды и пределы](../../Technical%20documentation/Testing.md#локальный-setup-этап-03), [устройство и полный inventory](../../Technical%20documentation/Local%20installation%20setup.md).

Отдельно не проверены настоящий второй OS-процесс, SQL/DDL/locking/atomicity, Ubuntu ACL/fsync/power loss, TLS/Kestrel/DNS/двусторонняя сеть, browser rendering и переключение работающего окружения. К следующим этапам реализация не переходила; внешняя приёмка остаётся в 07.
