# Тестирование

## Проверка этапа 08

2026-10-29 реализованы 08.1–08.5; согласованный минимальный порядок этапа 10 использован внутри 08. Самостоятельные 09/10 не начинались. Четыре production-проекта отдельно и последовательно собраны Debug --no-restore: 0 предупреждений и ошибок. Web target компилировал Razor и строгий TypeScript без hosting. Test builds общих проектов также последовательны.

| Набор | Успешно | Ошибки | Пропуски |
| --- | --- | --- | --- |
| Domain, весь изолированный проект | 36 | 0 | 0 |
| Application, весь изолированный проект | 215 | 0 | 0 |
| Infrastructure, выбранные ниже классы | 165 | 0 | 0 |
| Web, выбранные ниже классы | 88 | 0 | 0 |
| Frontend, два файла ниже | 15 | 0 | 0 |

Статическая проверка: 102 новых/изменённых файла проверены строгим UTF-8 decoder; нет U+FFFD, последовательностей из четырёх вопросительных знаков и обнаруженных признаков mojibake. Исходные BOM и LF/CRLF сохранены. Новые/изменённые C#/TypeScript-файлы меньше 20000 байт. Проверены 177 файловых ссылок и 38 anchors изменённой документации; все 81 Markdown-документ включены в solution items, отсутствующих файлов нет. 34 документа завершённых этапов 01–07 и 13 документов 09–10 побайтово совпадают с исходным снимком. Удалённых файлов нет; solution, migrations и конфигурационные примеры не менялись.

Всего 504 .NET и 15 frontend тестов; предыдущие этапы отдельно не суммируются. Новые Administration tests проверяют реальные query/factory/Pending/reducer, safe MVC DTO, active/anonymous/disabled API до I/O и после async read, одинаковые ресурсы, distinct accepted/Pending, retry/lost response, Replace/Cancel race и replacement outcome, held backup, fresh base/conflict resolution, глобальное удаление, observers и NodeChatRoute. Account controls проверяют новые credentials и прежние guards. HTTP 202 проверен прямым вызовом контроллера как durable очередь без sender success. Диагностика отличает follower/локальную БД/потерю peers, stale Telegram и утрату актуального leader.

Frontend вызывает production editor/draft/refresh с DOM stubs и compiled API с fake fetch: неизменный OperationId, сохранение ввода при ошибке сети/конфликте/потере ответа, явный rebase, held confirmation, пароль только в памяти, stale responses и повторные защищённые reads после reconnect, antiforgery/202/403. Регрессии 04–07 включают авторизацию/session epoch/RecoveryVersion/latched fault, accepted delivery gate/историю/retry/late success/replay.

Финальные команды из корня репозитория:

```powershell
dotnet build .\UptimeHarbor.Domain\UptimeHarbor.Domain.csproj -c Debug --no-restore -p:BaseOutputPath=D:\Media\User\source\repos\uptime-harbor\artifacts\compile-check\domain\
dotnet build .\UptimeHarbor.Application\UptimeHarbor.Application.csproj -c Debug --no-restore -p:BaseOutputPath=D:\Media\User\source\repos\uptime-harbor\artifacts\compile-check\application\
dotnet build .\UptimeHarbor.Infrastructure\UptimeHarbor.Infrastructure.csproj -c Debug --no-restore -p:BaseOutputPath=D:\Media\User\source\repos\uptime-harbor\artifacts\compile-check\infrastructure\
dotnet build .\UptimeHarbor.Web\UptimeHarbor.Web.csproj -c Debug --no-restore -p:BaseOutputPath=D:\Media\User\source\repos\uptime-harbor\artifacts\compile-check\web\
dotnet test --project .\tests\UptimeHarbor.Domain.Tests\UptimeHarbor.Domain.Tests.csproj -c Debug --no-restore -p:BaseOutputPath=D:\Media\User\source\repos\uptime-harbor\artifacts\compile-check\domain-tests\
dotnet test --project .\tests\UptimeHarbor.Application.Tests\UptimeHarbor.Application.Tests.csproj -c Debug --no-restore -p:BaseOutputPath=D:\Media\User\source\repos\uptime-harbor\artifacts\compile-check\application-tests\
dotnet test --project .\tests\UptimeHarbor.Infrastructure.Tests\UptimeHarbor.Infrastructure.Tests.csproj -c Debug --no-restore -p:BaseOutputPath=D:\Media\User\source\repos\uptime-harbor\artifacts\compile-check\infrastructure-tests\ -- --filter-class UptimeHarbor.Tests.Infrastructure.Identity.AcceptedSignInTests --filter-class UptimeHarbor.Tests.Infrastructure.Identity.AcceptedCookieTests --filter-class UptimeHarbor.Tests.Infrastructure.Identity.AccountBootstrapTests --filter-class UptimeHarbor.Tests.Infrastructure.Cluster.* --filter-class UptimeHarbor.Tests.Infrastructure.Persistence.ProjectionTransactionTests --filter-class UptimeHarbor.Tests.Infrastructure.Persistence.SharedStorageModelTests --filter-class UptimeHarbor.Tests.Infrastructure.Persistence.SharedStorageMigrationTests --filter-class UptimeHarbor.Tests.Infrastructure.Persistence.SharedStorageRegistrationTests --filter-class UptimeHarbor.Tests.Infrastructure.Monitoring.HttpMonitoringEndpointCheckerTests --filter-class UptimeHarbor.Tests.Infrastructure.Monitoring.MonitoringCycleRunnerTests --filter-class UptimeHarbor.Tests.Infrastructure.Telegram.TelegramHttpNotificationSenderTests
dotnet test --project .\tests\UptimeHarbor.Web.Tests\UptimeHarbor.Web.Tests.csproj -c Debug --no-restore -p:BaseOutputPath=D:\Media\User\source\repos\uptime-harbor\artifacts\compile-check\web-tests\ -- --filter-class UptimeHarbor.Tests.Web.Cluster.* --filter-class UptimeHarbor.Tests.Web.DependencyInjection.* --filter-class UptimeHarbor.Tests.Web.AdminUsersControllerAuthorizationTests --filter-class UptimeHarbor.Tests.Web.AdminNotificationsControllerAuthorizationTests --filter-class UptimeHarbor.Tests.Web.ReadContractSecurityTests --filter-class UptimeHarbor.Tests.Web.WebPerimeterSecurityTests --filter-class UptimeHarbor.Tests.Web.DashboardSignalRTests --filter-class UptimeHarbor.Tests.Web.ModelStateErrorContractTests --filter-class UptimeHarbor.Tests.Web.JsonResultMapperTests --filter-class UptimeHarbor.Tests.Web.HttpClientContractTests
node --experimental-strip-types --test tests/Frontend/clusterAdministration.test.mts tests/Frontend/searchableSelects.test.mts
```

Новой migration нет; исторические migrations/designer/snapshot не изменялись, обе подготовленные migrations не применялись. Default Disabled и admission/trust/activation/manual-enable/sender/delivery gates сохранены. Git, SQL/БД (включая SQLite), provider tests, приложение/hosting, Docker, реальные peers/monitoring endpoints/Telegram и deployment не запускались. Bootstrap/mobile/CSP/WebSocket/browser acceptance, SQL transaction semantics/locking, crash/fsync/живая сеть и exactly-once не подтверждены. Часовой догон трёх узлов/около 30 приложений остаётся целью отдельного наблюдения.

## Проверка этапа 07

Статическая проверка: 94 новых/изменённых файла проверены строгим UTF-8 decoder; нет U+FFFD, последовательностей из четырёх знаков вопроса и обнаруженных признаков mojibake. Исходные LF/CRLF и BOM сохранены без массовой нормализации; новые/изменённые C# файлы меньше 20000 байт. Проверены Markdown-документы (81) и файловые ссылки (782), битых ссылок/отсутствующих solution items/не включённых документов нет. 29 документов завершённых этапов 01–06 и 19 документов 08–10 побайтово совпадают с исходным снимком задачи. Новых документов нет; solution items не менялись.

2026-10-29 выполнены 07.1–07.4 в согласованном минимальном порядке этапа 10; самостоятельный 10 не начинался. Четыре production-проекта собраны отдельно последовательно в Debug без restore: 0 предупреждений/ошибок. Web compile-check выполнил существующий TypeScript target без host. Сборки и test builds общих проектов не выполнялись параллельно.

| Набор | Успешно | Ошибки | Пропуски |
| --- | --- | --- | --- |
| Domain, весь изолированный проект | 36 | 0 | 0 |
| Application, весь изолированный проект | 200 | 0 | 0 |
| Infrastructure, только перечисленные классы | 165 | 0 | 0 |
| Web, только перечисленные классы | 55 | 0 | 0 |

Всего 456 успешных тестов; результаты 399 тестов этапа 06 ниже сохранены как история, в этот итог не суммируются. Полный Infrastructure-набор и provider tests не запускались.

Фактические финальные команды из корня репозитория:

```powershell
dotnet build .\UptimeHarbor.Domain\UptimeHarbor.Domain.csproj -c Debug --no-restore -p:BaseOutputPath=D:\Media\User\source\repos\uptime-harbor\artifacts\compile-check\domain\
dotnet build .\UptimeHarbor.Application\UptimeHarbor.Application.csproj -c Debug --no-restore -p:BaseOutputPath=D:\Media\User\source\repos\uptime-harbor\artifacts\compile-check\application\
dotnet build .\UptimeHarbor.Infrastructure\UptimeHarbor.Infrastructure.csproj -c Debug --no-restore -p:BaseOutputPath=D:\Media\User\source\repos\uptime-harbor\artifacts\compile-check\infrastructure\
dotnet build .\UptimeHarbor.Web\UptimeHarbor.Web.csproj -c Debug --no-restore -p:BaseOutputPath=D:\Media\User\source\repos\uptime-harbor\artifacts\compile-check\web\
dotnet test --project .\tests\UptimeHarbor.Domain.Tests\UptimeHarbor.Domain.Tests.csproj -c Debug --no-restore -p:BaseOutputPath=D:\Media\User\source\repos\uptime-harbor\artifacts\compile-check\domain-tests\
dotnet test --project .\tests\UptimeHarbor.Application.Tests\UptimeHarbor.Application.Tests.csproj -c Debug --no-restore -p:BaseOutputPath=D:\Media\User\source\repos\uptime-harbor\artifacts\compile-check\application-tests\
dotnet test --project .\tests\UptimeHarbor.Infrastructure.Tests\UptimeHarbor.Infrastructure.Tests.csproj -c Debug --no-restore -p:BaseOutputPath=D:\Media\User\source\repos\uptime-harbor\artifacts\compile-check\infrastructure-tests\ -- --filter-class UptimeHarbor.Tests.Infrastructure.Identity.AcceptedSignInTests --filter-class UptimeHarbor.Tests.Infrastructure.Identity.AcceptedCookieTests --filter-class UptimeHarbor.Tests.Infrastructure.Identity.AccountBootstrapTests --filter-class UptimeHarbor.Tests.Infrastructure.Cluster.* --filter-class UptimeHarbor.Tests.Infrastructure.Persistence.ProjectionTransactionTests --filter-class UptimeHarbor.Tests.Infrastructure.Persistence.SharedStorageModelTests --filter-class UptimeHarbor.Tests.Infrastructure.Persistence.SharedStorageMigrationTests --filter-class UptimeHarbor.Tests.Infrastructure.Persistence.SharedStorageRegistrationTests --filter-class UptimeHarbor.Tests.Infrastructure.Monitoring.HttpMonitoringEndpointCheckerTests --filter-class UptimeHarbor.Tests.Infrastructure.Monitoring.MonitoringCycleRunnerTests --filter-class UptimeHarbor.Tests.Infrastructure.Telegram.TelegramHttpNotificationSenderTests
dotnet test --project .\tests\UptimeHarbor.Web.Tests\UptimeHarbor.Web.Tests.csproj -c Debug --no-restore -p:BaseOutputPath=D:\Media\User\source\repos\uptime-harbor\artifacts\compile-check\web-tests\ -- --filter-class UptimeHarbor.Tests.Web.Cluster.* --filter-class UptimeHarbor.Tests.Web.DependencyInjection.* --filter-class UptimeHarbor.Tests.Web.AdminUsersControllerAuthorizationTests --filter-class UptimeHarbor.Tests.Web.AdminNotificationsControllerAuthorizationTests --filter-class UptimeHarbor.Tests.Web.ReadContractSecurityTests --filter-class UptimeHarbor.Tests.Web.WebPerimeterSecurityTests --filter-class UptimeHarbor.Tests.Web.DashboardSignalRTests
```

DeliveryLedgerTests/Monitoring tests вызывают production reducer/projection: непрерывные реальные attempts, stable keys и binding generation/rebind, NodeId routes, известный success/replay, retry_after и terminal exhaustion, ambiguous старую попытку после failover и поздний success, отмены delete/disable/endpoint/reset, recovery entitlement только уведомлённым чатам, summary composition и сохранность исходных событий. RecoveryMessage-проверка различает partial/full и metadata по узлам. Configuration reset проверяется как принятая смена CheckGeneration с параметрами; отдельного UI reset этапа 08 не добавлено.

AcceptedDeliveryTests вызывает production dispatcher, gate, DeliverySenderConsensus, RevisionDispatcher и reducer с изолированными storage/time/Telegram doubles. Проверены восстановление очереди без локального outbox/сессии, потеря majority до transport, term после claim, process pause, unknown recovery/delivery/authority dependencies, повторная авторизация теста после disable, независимость медленного/запрещённого чата, timeout без фиктивного success, внешний success до принятия результата, потерянный ответ локального commit/accepted result, повторная передача capture после restart без повторного transport/success-аудита и настоящий in-flight outage success после unbind/recovered с одним заключительным recovery.

ClusterTestDeliveryTests проходит follower durable Pending → production pump → изолированный transport → leader RevisionDispatcher/reducer → sender. Проверяются доверенный actor/origin, отсутствие отправки на follower и повторная авторизация при отключении до принятия. TelegramHttpNotificationSenderTests использует fake HttpMessageHandler: message_id обязателен для accepted success, retry_after сохраняется без response text/секретов. Это не реальный Telegram вызов.

MonitoringRecoveryTests дополняет настоящий локальный DotNext snapshot/WAL тест commands BeginDelivery/RecordDelivery, duplicate result и reopen/replay: один attempt/evidence, известный success не становится due, полная projection сверяется metadata-only context с закрытым connection. Snapshot/recovery не восстанавливает локальное authority и не открывает delivery readiness. Тест подготовки metadata исправлен для единственной общей роли нескольких аккаунтов, без обхода snapshot uniqueness guard. Composition tests разрешают реальный Web graph, включая sender consensus/worker, без DI цикла и без host/WAL/HTTP. Регрессии production pipeline 04–06, авторизации, session epoch/RecoveryVersion и latched apply fault включены указанными наборами.

Новая migration не требуется; исторические migrations/designer/model snapshot не изменены, обе подготовленные migrations к БД не применялись. Default Disabled сохранён, admission/trust/activation/delivery proof и общие gates не открывались. UI 08, provisioning/fencing/controlled enable 09 и самостоятельный 10 не начаты.

Не запускались БД/SQL, включая SQLite, приложение/hosting, Docker, реальные peers/monitoring endpoints/Telegram, deployment или Git. Fake storage не доказывает SQL transaction semantics/locking; локальный WAL reopen не доказывает power-loss/crash-fsync или живой consensus/сеть; fake sender не доказывает exactly-once; статические/DI tests не являются browser acceptance. Полный стенд, нагрузка и тест для каждой строки матрицы не проводились. Часовая цель догона трёх узлов/около 30 приложений остаётся целью отдельного наблюдения.

## Проверка этапа 06

Финальная статическая проверка: 101 новый/изменённый файл проверен строгим UTF-8 decoder, без U+FFFD и обнаруженных признаков mojibake; сохранены исходные LF/CRLF, массовой нормализации нет. Все новые/изменённые C# файлы меньше 20000 байт. Проверены 81 Markdown-документ и 763 файловые ссылки: битых ссылок, отсутствующих solution items и не включённых документов нет. 24 документа завершённых этапов 01–05 побайтово совпадают с исходным снимком задачи; этапы 07–10 не менялись. Новые документы не добавлялись, поэтому изменения solution items не потребовались.

Дополнительная проверка unbind выявила guard, ошибочно считавший deletion изменением identity binding; guard ограничен редактированием. Тесты теперь проверяют Committed у самого unbind. Проверены отзыв неотправленной сводки, сохранение её состава и новый план для остальных источников. Старое evidence требует настоящей accepted конфигурации/назначения; fabricated generation/observer отвергаются.

2026-10-29 завершены 06.1–06.4. Четыре production-проекта собраны последовательно и отдельно в Debug без restore: 0 предупреждений/ошибок. После исправления границы DI повторены затронутые Infrastructure/Web builds и проверки. Общий obj не изолируется разным BaseOutputPath; параллельных builds не было.

| Набор | Успешно | Ошибки | Пропуски |
| --- | --- | --- | --- |
| Domain, весь изолированный проект | 36 | 0 | 0 |
| Application, весь изолированный проект | 185 | 0 | 0 |
| Infrastructure, только перечисленные классы | 127 | 0 | 0 |
| Web, только перечисленные классы | 51 | 0 | 0 |

Всего 399 успешных проверок. Это свежие результаты этапа 06, включая регрессии production pipeline и account/session/recovery-version 04/05. Полный Infrastructure-набор с provider tests не запускался.

Фактические финальные команды из корня репозитория:

```powershell
dotnet build .\UptimeHarbor.Domain\UptimeHarbor.Domain.csproj -c Debug --no-restore -p:BaseOutputPath=D:\Media\User\source\repos\uptime-harbor\artifacts\compile-check\domain\
dotnet build .\UptimeHarbor.Application\UptimeHarbor.Application.csproj -c Debug --no-restore -p:BaseOutputPath=D:\Media\User\source\repos\uptime-harbor\artifacts\compile-check\application\
dotnet build .\UptimeHarbor.Infrastructure\UptimeHarbor.Infrastructure.csproj -c Debug --no-restore -p:BaseOutputPath=D:\Media\User\source\repos\uptime-harbor\artifacts\compile-check\infrastructure\
dotnet build .\UptimeHarbor.Web\UptimeHarbor.Web.csproj -c Debug --no-restore -p:BaseOutputPath=D:\Media\User\source\repos\uptime-harbor\artifacts\compile-check\web\
dotnet test --project .\tests\UptimeHarbor.Domain.Tests\UptimeHarbor.Domain.Tests.csproj -c Debug --no-restore -p:BaseOutputPath=D:\Media\User\source\repos\uptime-harbor\artifacts\compile-check\domain-tests\
dotnet test --project .\tests\UptimeHarbor.Application.Tests\UptimeHarbor.Application.Tests.csproj -c Debug --no-restore -p:BaseOutputPath=D:\Media\User\source\repos\uptime-harbor\artifacts\compile-check\application-tests\
dotnet test --project .\tests\UptimeHarbor.Infrastructure.Tests\UptimeHarbor.Infrastructure.Tests.csproj -c Debug --no-restore -p:BaseOutputPath=D:\Media\User\source\repos\uptime-harbor\artifacts\compile-check\infrastructure-tests\ -- --filter-class UptimeHarbor.Tests.Infrastructure.Identity.AcceptedSignInTests --filter-class UptimeHarbor.Tests.Infrastructure.Identity.AcceptedCookieTests --filter-class UptimeHarbor.Tests.Infrastructure.Identity.AccountBootstrapTests --filter-class UptimeHarbor.Tests.Infrastructure.Cluster.* --filter-class UptimeHarbor.Tests.Infrastructure.Persistence.ProjectionTransactionTests --filter-class UptimeHarbor.Tests.Infrastructure.Persistence.SharedStorageModelTests --filter-class UptimeHarbor.Tests.Infrastructure.Persistence.SharedStorageMigrationTests --filter-class UptimeHarbor.Tests.Infrastructure.Persistence.SharedStorageRegistrationTests --filter-class UptimeHarbor.Tests.Infrastructure.Monitoring.HttpMonitoringEndpointCheckerTests --filter-class UptimeHarbor.Tests.Infrastructure.Monitoring.MonitoringCycleRunnerTests
dotnet test --project .\tests\UptimeHarbor.Web.Tests\UptimeHarbor.Web.Tests.csproj -c Debug --no-restore -p:BaseOutputPath=D:\Media\User\source\repos\uptime-harbor\artifacts\compile-check\web-tests\ -- --filter-class UptimeHarbor.Tests.Web.Cluster.* --filter-class UptimeHarbor.Tests.Web.DependencyInjection.* --filter-class UptimeHarbor.Tests.Web.AdminUsersControllerAuthorizationTests --filter-class UptimeHarbor.Tests.Web.ReadContractSecurityTests --filter-class UptimeHarbor.Tests.Web.WebPerimeterSecurityTests --filter-class UptimeHarbor.Tests.Web.DashboardSignalRTests
```

Application MonitoringAvailability/Incident/Contract tests вызывают фактические reducer/write set: assigned observers, независимые thresholds/success reset, generation/stale/deleted/disabled/gap/id reuse, unknown clock, availability отдельно от coverage, metadata по observers (включая разное время запуска), node routes и правильный NodeId. Проверены стабильные partial/full/recovery IDs и порядок, отсутствие дублей событий/intents при replay, отсутствие recovery неуведомлённому чату, сохранение права после unbind и отмена при удалении чата. Обычный partial не обращается к election/resign.

ObservationCaptureTests вызывает настоящий ObservationCaptureService и ObservationCaptureUnitOfWork на транзакционном storage/session double: отказ до commit, потеря ответа после commit, retry того же ID, восстановление хранилища/sequence и отказ changed/stale payload. Fake storage не доказывает SQL Server transaction semantics, constraints, multi-process locking или физическую durability.

MonitoringRuntimeTests вызывает production pump/cycle/peer capture/dispatcher: accepted frontier ACK после потери ответа и restart, сохранение local capture, назначенные параллельные проверки на fake HttpMessageHandler, отсутствие проверок unassigned/Disabled, DB failure и запрет возврата до ConfirmRecovery. Проверены PeerUnavailable отдельно от сообщённых DatabaseUnavailable/WorkerStalled/TelegramUnavailable, source NodeId, отсутствие success-аудита при failed apply, один audit при retry/replay и отказ follower coordinator. Тестовый accepted proof из harness не переносится в production registrations.

MonitoringCoordinatorTests проверяет historical budget, отсутствие live до обработки очереди временных точек, follower deny/new term continuation, независимый DeliveryStateConfirmed и две порции давнего offline backlog в одном term. Durable summaries сохраняют cut/source IDs и SuppressedBySummary, не выдают recovery без подтверждения отправки. Открытые аварии и recovery ранее уведомлённых чатов проверены reducer tests; возрастного отсечения нет.

MonitoringRecoveryTests использует настоящий snapshot file и DotNext WAL: outage snapshot → success/evaluate/repeat → dispose/reopen → production recovery/reducer → подтверждение committed/applied/membership. Incident ID и порядок сохраняются, observations не теряются, repeated evaluate не создаёт дубль. Snapshot metadata сопоставлены с monitoring rows; отсутствие observation или настоящего delivery evidence отвергается. Domain DeliveryAttempt в fixture отражает принятую успешную попытку; production producer attempts не добавлен. Подставная projection не доказывает SQL snapshot install/atomicity, native elections/majority, crash/fsync или power loss.

ProjectionTransactionTests включает production monitoring incident/event/intents в одну границу существующего UnitOfWork с cursor/receipt, проверяет fault до/после fake commit, replay без повторных эффектов и сохранение latched apply fault. Прежние account/recovery-version tests также успешны; новый Invalidate не позволяет завершить старую recovery-проверку успехом.

Web RevisionBoundaryTests проверяет совпадение monitoring storage generation с authenticated peer envelope до dispatch. DI-регрессия сначала выявила регистрацию pump/coordinator в Infrastructure без Web transport/dispatcher; production composition исправлен, все выбранные Web tests успешны. Это проверка графа/контракта без Start host, а не browser/WebSocket acceptance.

Схема не менялась: новая migration не требуется, historical migrations/designer/snapshot вручную не изменялись, обе подготовленные migrations 02/04 к БД не применялись. Не запускались SQL/БД (включая SQLite), provider tests, приложение/hosting, Docker, реальные peers/endpoints/Telegram, deployment или Git. Dispatcher/attempt producer/sender lifecycle 07, UI 08, provisioning/fencing/controlled enable 09 не реализованы; закрытые dependencies/gates и Disabled сохранены. Этап 10 отдельно не начинался: его согласованный минимальный порядок использован внутри 06. Часовая цель догона трёх узлов/около 30 приложений сохранена для самостоятельного наблюдения, нагрузка и внешняя приёмка не проведены.

## Проверка этапа 03

2026-10-29 production-проекты Domain, Application, Infrastructure и Web собраны отдельными `dotnet build -c Debug --no-restore`, каждый в собственный compile-check output: 0 warnings / 0 errors. Restore Web, Infrastructure.Tests и Web.Tests с DotNext 6.10.0 выполнен успешно. Ни host, ни узлы не запускались.

| Набор | Passed | Failed | Skipped |
| --- | --- | --- | --- |
| Application, весь изолированный проект | 139 | 0 | 0 |
| Infrastructure, только `UptimeHarbor.Tests.Infrastructure.Cluster.*` | 38 | 0 | 0 |
| Web, только `UptimeHarbor.Tests.Web.Cluster.*` | 16 | 0 | 0 |

Всего 193 passed. Domain-код в 03 не менялся; прежние 36 Domain и 27 Infrastructure этапа 02 не выдаются за новые результаты. Полный Infrastructure-набор с SQLite provider tests не запускался.

Фактические команды из корня репозитория:

```powershell
dotnet build .\UptimeHarbor.Domain\UptimeHarbor.Domain.csproj -c Debug --no-restore -p:BaseOutputPath=D:\Media\User\source\repos\uptime-harbor\artifacts\compile-check\domain\
dotnet build .\UptimeHarbor.Application\UptimeHarbor.Application.csproj -c Debug --no-restore -p:BaseOutputPath=D:\Media\User\source\repos\uptime-harbor\artifacts\compile-check\application\
dotnet build .\UptimeHarbor.Infrastructure\UptimeHarbor.Infrastructure.csproj -c Debug --no-restore -p:BaseOutputPath=D:\Media\User\source\repos\uptime-harbor\artifacts\compile-check\infrastructure\
dotnet build .\UptimeHarbor.Web\UptimeHarbor.Web.csproj -c Debug --no-restore -p:BaseOutputPath=D:\Media\User\source\repos\uptime-harbor\artifacts\compile-check\web\
dotnet test --project .\tests\UptimeHarbor.Application.Tests\UptimeHarbor.Application.Tests.csproj -c Debug --no-restore -p:BaseOutputPath=D:\Media\User\source\repos\uptime-harbor\artifacts\compile-check\application-tests\
dotnet test --project .\tests\UptimeHarbor.Infrastructure.Tests\UptimeHarbor.Infrastructure.Tests.csproj -c Debug --no-restore -p:BaseOutputPath=D:\Media\User\source\repos\uptime-harbor\artifacts\compile-check\infrastructure-tests\ -- --filter-class UptimeHarbor.Tests.Infrastructure.Cluster.*
dotnet test --project .\tests\UptimeHarbor.Web.Tests\UptimeHarbor.Web.Tests.csproj -c Debug --no-restore -p:BaseOutputPath=D:\Media\User\source\repos\uptime-harbor\artifacts\compile-check\web-tests\ -- --filter-class UptimeHarbor.Tests.Web.Cluster.*
```

Application Cluster tests вызывают production readiness, health-quorum, gate и membership coordinator: singleton authority с accepted proof, role-only deny, потеря majority/generation/epoch/readiness, DB fault и repair без старого proof, неподтверждённый catch-up/account, 30 с без продления неуспехом, 3+2/minority/asymmetric/stale witnesses, clock jump/pause/restart, drain/expiry/late proofs и отмена in-flight scopes. Membership проверяет локальный/принятый активный аккаунт, запрет последнего участника, достоверные actor/node IDs и отсутствие успеха до protocol result + accepted receipt. Подставной consensus проверяет контракт gate/coordinator, не выборы DotNext.

Infrastructure Cluster tests используют настоящий DotNext WriteAheadLog в временном каталоге: reopen term/vote/committed payload и callback apply. Локальный Commit — только storage-подготовка, не имитация majority. DispatchProxy перехватывает фактический интерфейс replication API, чтобы проверить wiring adapter и ожидание apply; он не реализует fake election. Проверены восстановление до WAL, exclusive lock, одноразовый bootstrap, отказ утраченного/усечённого state, peer envelope/compatibility/identity/replay/limits, настоящий тестовый CA/certificate/SAN/pin, native sender/term, fake HTTP challenge/timeout/no cookies, Telegram getMe/BotId и безопасный audit. Web Cluster tests проверяют настоящий persistent configuration reopen с 1/3 voters, middleware/listener boundary/oversize, закрытый recovery и DI без host/WAL/HTTP.

Первый Web Cluster прогон выявил ошибку test assertion для отсутствующего StringValues header; исправлена проверка Count. Ошибка компиляции тестовой подготовки persistent configuration исправлена по реальному API Load/Add/Save. Изолированное создание настоящего DotNext cluster/store/native pipeline выявило недостающий DiagnosticListener в child DI; добавлены diagnostic/metrics зависимости ASP.NET, повторная проверка прошла без Start и без запросов (fake handler запрещает сеть). Финальные наборы прошли без предупреждений. Более ранний положительный certificate тест выявил сравнение local certificate dates с UTC; production сравнение приведено к UTC и повторно проверено.

Reopen не доказывает fsync/power-loss/crash recovery. Не выполнялись реальные выборы/одновременные кандидаты/majority commit на узлах, peer HTTPS/Nginx/CRL, network partitions, OS suspend, SQL recovery/миграции, браузер, мониторинг, Telegram, Docker или deployment. Полный стенд и нагрузка не требовались; внешние наблюдения учитываются отдельно по согласованному этапу 10. `Unavailable*` порты не заменялись фиктивным успехом, controlled enable не выполнялся. Проверены ссылки/solution items/UTF-8; документы 01/02 не переписаны, Git не использовался.

В 03 дополнительно проверен replay/apply fault на настоящем WAL: ошибка остаётся latched при последующем успешном DB connectivity probe, health сохраняет отдельные ProjectionUnavailable и возраст, а provider payload не появляется в исключении DotNext. Это проверка закрытия повреждённой проекции, не её восстановления средствами будущего 04.

## Test stack

Четыре test-проекта в каталоге `tests` используют общие настройки из `tests/Directory.Build.props`:

- `xunit.v3` 4.0.0;
- Visual Studio runner 4.0.0;
- Microsoft.NET.Test.Sdk 18.9.0;
- Microsoft Testing Platform;
- расширения code coverage и TRX report.

Корневой `global.json` выбирает .NET SDK 10.0.400 и runner `Microsoft.Testing.Platform`.

Перечисленные выше версии — базовые из tests/Directory.Build.props. Текущие test csproj переопределяют xunit.v3 4.0.2, runner 4.0.1, Test.Sdk 18.11.0, CodeCoverage 18.12.0 и TrxReport 2.5.1.

## Запуск

Каждый набор запускается отдельно:

```powershell
dotnet test --project .\tests\UptimeHarbor.Domain.Tests\UptimeHarbor.Domain.Tests.csproj
dotnet test --project .\tests\UptimeHarbor.Application.Tests\UptimeHarbor.Application.Tests.csproj
dotnet test --project .\tests\UptimeHarbor.Infrastructure.Tests\UptimeHarbor.Infrastructure.Tests.csproj
dotnet test --project .\tests\UptimeHarbor.Web.Tests\UptimeHarbor.Web.Tests.csproj
```

Старый синтаксис `dotnet test <project>` включает VSTest target и несовместим с выбранным runner на .NET 10.

Перед тестами затронутые production- и test-проекты собираются отдельно. Compile-check выполняется без запуска Web-хоста и с отдельным `BaseOutputPath` в `artifacts/compile-check`.

Статические Web-тесты ищут исходники на пять уровней выше `AppContext.BaseDirectory`. Для них отдельный output должен сохранять эту глубину, например `artifacts/compile-check/web-tests/Debug/net10.0`. Общий `artifacts/compile-check/Debug/net10.0` подходит для компиляции, но для Web source tests приводит к ошибкам поиска файлов. Корректировать нужно путь проверочного output, не создавать копии исходников за пределами решения.

## Выбор проверок при запрете БД

Полный Infrastructure-набор открывает SQLite in-memory и исполняет SQL. При запрете подключения к БД он не запускается целиком. Допустимый набор metadata/fake проверок выбирается повторяемым аргументом `--filter-class` после `--` в команде Microsoft Testing Platform:

- `UptimeHarbor.Tests.Infrastructure.Identity.InitialAdministratorBootstrapperTests`;
- `UptimeHarbor.Tests.Infrastructure.Monitoring.HttpMonitoringEndpointCheckerTests`;
- `UptimeHarbor.Tests.Infrastructure.Monitoring.MonitoringCycleRunnerTests`;
- `UptimeHarbor.Tests.Infrastructure.Persistence.UptimeHarborDbContextModelTests`;
- `UptimeHarbor.Tests.Infrastructure.Persistence.SqlServerPersistenceConflictClassifierTests`;
- `UptimeHarbor.Tests.Infrastructure.Persistence.SharedStorageModelTests`;
- `UptimeHarbor.Tests.Infrastructure.Persistence.SharedStorageMigrationTests`;
- `UptimeHarbor.Tests.Infrastructure.Persistence.ProjectionTransactionTests`;
- `UptimeHarbor.Tests.Infrastructure.Persistence.SharedStorageRegistrationTests`;
- `UptimeHarbor.Tests.Infrastructure.Telegram.TelegramHttpNotificationSenderTests`.

Metadata-тест создаёт SQL Server options и читает EF model без открытия соединения. Sign-in, materialized reads, concurrency, unique constraints и Telegram outbox provider tests в этот выбор не входят; их нельзя считать пройденными или пропущенными runner, если они исключены фильтром. Результат обновления локальных DLL и точная граница последней проверки приведены в [Libraries.md](Libraries.md).

## Структура и состав наборов

- `UptimeHarbor.Domain.Tests` — доменные переходы и фабрики;
- `UptimeHarbor.Application.Tests` — use case, политики, DTO и Application DI;
- `UptimeHarbor.Infrastructure.Tests` — persistence metadata и SQLite provider behavior, Identity bootstrap, monitoring/outbox workers и внешние адаптеры на fake transport;
- `UptimeHarbor.Web.Tests` — Web DI/configuration, authorization/antiforgery metadata, JSON mapping, SignalR и статические frontend-инварианты.
- `tests/Frontend/*.test.mts` — изолированные исполняемые проверки TypeScript через `node --experimental-strip-types --test tests/Frontend/*.test.mts`, без приложения и браузера.

Совокупный набор проверяет:

- доменные переходы серверов и контейнеров;
- validation options и DI регистрации;
- инфраструктурные registrations без реального подключения к БД;
- EF model metadata, каскады, ключи и indexes;
- optimistic concurrency через два независимых `UptimeHarborDbContext` на SQLite in-memory provider: monitoring/disable, monitoring/endpoint и два admin updates;
- UTC converter;
- notification binding factories;
- bootstrap администратора и повторный запуск;
- управление пользователями и permissions;
- единый `IAdministratorAccessPolicy`: unauthenticated, disabled, ordinary и administrator outcomes, одинаковая семантика трёх защищённых сервисов и отсутствие repository/UoW/save/Telegram/publisher вызовов при отказе;
- полная и ограниченная видимость, прямой доступ и блокировка;
- role metadata административного контроллера;
- административные сценарии серверов и контейнеров, валидация, независимые переключатели и уведомление после сохранения;
- role и antiforgery metadata административного API объектов;
- HTTP/JSON mapping всех типов `ServiceError`;
- полная DI validation Web composition root;
- DI-регистрацию database check-up/backup services и статический порядок automatic migration до Identity bootstrap;
- статические контракты Web-конфигурации: обязательный `Config/config.json` до DI, изоляция `appsettings.json` для Serilog, полнота и безопасность `config.example.json`, точное git-ignore правило и запрет копирования обоих Config JSON в runtime/publish artifacts;
- фактическое построение Serilog logger из `appsettings.json` без запуска Web-host и без записи log events;
- единый человекочитаемый UTC-шаблон console/rolling File sinks, непубличный относительный path, daily/size rolling, retention, исключение `Logs` из artifacts и защищённый Docker volume;
- наличие trusted forwarded headers до routing и отсутствие внутреннего HTTPS redirect в Web pipeline за Nginx;
- HTTP/JSON-контракт server/container через fake `HttpMessageHandler`, включая status, timeout/cancellation во время чтения, network, 64 KiB limit, chunked body, redirect policy и все обязательные поля;
- application-переходы monitoring engine, независимость server/container и порядок save→publish;
- параллельность разных объектов, keyed-защита одной цели, изоляция ошибки и cancellation hosted worker.
- dashboard query для администратора и пользователя, независимые container permissions, поиск, fallback имени, UTC и uptime через фиксированный `TimeProvider`;
- серверное определение SignalR-получателей, минимальный payload, изоляция push-ошибки, auth metadata и reconnect/snapshot lifecycle клиента.
- пакетное чтение recipient permissions без per-user вызовов и прежнюю server/container visibility semantics;
- read-only signatures всех списковых Application repository contracts, отсутствие `IQueryable` в публичных Application-контрактах и materialized dashboard/recipient read models;
- SQLite-проверку специализированных dashboard/recipient Infrastructure queries и отсутствия endpoint URL в projection; после удаления ресурсных таблиц recipient query получает пользователей/роль одним запросом. Адаптированный provider-тест в этапе 02 не запускался;
- SQLite-проверку уникального нормализованного имени сервера и metadata-проверку обязательных полей `Name`/`NormalizedName`;
- отсутствие endpoint URL в dashboard DTO, сохранение endpoint в admin DTO и отсутствие controller route `/api/objects`;
- best-effort administrative SignalR publisher: commit остаётся успешным при transport exception, ошибка журналируется, а до commit/conflict publisher не вызывается;
- фактически зарегистрированную `InvalidModelStateResponseFactory` и MVC JSON options для malformed body, отсутствующего body, неверного типа и field validation без parser details;
- безопасные ветви TypeScript HTTP helper для JSON/empty/non-JSON errors и `AbortError`, а также исходный coalescing/`AbortController`/reconnect wiring;
- Identity lockout через реальный `SignInManager` и SQLite in-memory store: порог, ограниченный срок, reset при успехе и независимость от `IsEnabled`;
- login-specific rate-limit metadata/rejection, Identity lockout metadata и выполнение middleware security headers/CSP без Web-host;
- per-binding состояние outage/recovery, частичный успех, повторная обработка только неуспешных получателей, независимые server/container bindings, fallback имени, parent server, UTC и uptime через fake `TimeProvider`;
- административные Telegram use case, роль и antiforgery metadata MVC API, AJAX/DOM-инварианты frontend;
- Telegram HTTP adapter через fake `HttpMessageHandler`, безопасные исключения и изоляция ошибки одного чата;
- формат Telegram token, отказ до HTTP при заглушке, корректный относительный Bot API URI и классификацию HTTP/JSON ошибок; Application проверяет безопасные сообщения тестовой отправки без утечки исходного текста исключения;
- геометрию ресайза через DOM-заглушку: номинальные ширины отличаются от фактических, pointerdown и повторный drag не меняют геометрию, дельта меняет только выбранную колонку, отмена восстанавливает ширину, клавиатура и минимальная ширина соблюдаются;
- durable outbox до/после нового DbContext, параллельное начало доставки чатов, независимость monitoring cycle и лимит пяти retry через управляемый `TimeProvider`;
- provider-level SQLite-сценарии recovery: outage marker → recovery queued → disable/endpoint change → terminal cancellation без sender, обычная recovery-доставка и безопасный pre-send skip уже claimed job;
- конкурентные duplicate chat/binding на двух SQLite `DbContext`, Application mapping независимого persistence conflict и безопасный HTTP 409 envelope без SQL/provider details;
- изолированный классификатор SQL Server 2601/2627 для известных Telegram entities, отказ классифицировать неизвестный номер/тип и проброс неизвестного `DbUpdateException`;
- отсутствие SignalR/outbox event после provider concurrency conflict и HTTP `409` для administrative conflict.

Web contract tests сериализуют `JsonResult` и administrative DTO через фактические MVC `JsonOptions`. Application проверяет self-disable/self-delete и last-enabled-admin. Runtime-проверка automatic SQL Server backup/migration, server-side backup permissions, concurrency, row-lock ordering dispatcher против administrative cancellation, фактические `SqlException` 2601/2627 вместе с EF entries, restart процесса и Telegram остаётся отдельной ручной проверкой; Docker/Nginx не входят в scope v1.

## Проверка разделения серверов и контейнеров

Проверены создание контейнера без сервера, привязка/отвязка с обеих страниц, конфликт версии/занятого контейнера и сохранение контейнеров при удалении сервера. Прежние проверки cleanup permissions остаются проверками совместимых вызовов: после этапа 02 этих таблиц нет. EF metadata подтверждает nullable ServerId и FK без подключения к БД. Проверки DOM-заглушек в `Frontend/searchableSelects.test.mts` проверяют пустые/disabled поля, checkbox/radio и снятие связи через «Без сервера». Браузер, SQL Server, применение migration и доставка Telegram остаются непроверенными.

## Проверка этапа 02

2026-10-29 выполнены отдельные compile-check `UptimeHarbor.Domain`, `Application`, `Infrastructure` и `Web` в Debug с `--no-restore` и раздельным `BaseOutputPath` внутри `artifacts/compile-check`: 0 предупреждений и ошибок. Web-host не запускался; существующий MSBuild target скомпилировал TypeScript.

| Набор | Успешно | Ошибки | Пропуски |
| --- | --- | --- | --- |
| Domain, весь изолированный проект | 36 | 0 | 0 |
| Application, весь изолированный проект | 111 | 0 | 0 |
| Infrastructure, только шесть выбранных классов ниже | 27 | 0 | 0 |

Фактическая команда Infrastructure из корня репозитория (одна строка):

```powershell
dotnet test --project .\tests\UptimeHarbor.Infrastructure.Tests\UptimeHarbor.Infrastructure.Tests.csproj -c Debug --no-restore -p:BaseOutputPath=D:\Media\User\source\repos\uptime-harbor\artifacts\compile-check\infrastructure-tests\ -- --filter-class UptimeHarbor.Tests.Infrastructure.Persistence.SharedStorageMigrationTests --filter-class UptimeHarbor.Tests.Infrastructure.Persistence.SharedStorageModelTests --filter-class UptimeHarbor.Tests.Infrastructure.Persistence.ProjectionTransactionTests --filter-class UptimeHarbor.Tests.Infrastructure.Persistence.SharedStorageRegistrationTests --filter-class UptimeHarbor.Tests.Infrastructure.Persistence.UptimeHarborDbContextModelTests --filter-class UptimeHarbor.Tests.Infrastructure.Identity.InitialAdministratorBootstrapperTests
```

Новые Domain-тесты проверяют login/self/last-admin, отдельные ревизии, terminal Pending/control, cursor, observer sequence, календарный год/protection flags, лимит попыток, известный успех и recovery entitlement. `SharedStorageModelTests` проверяет metadata/индексы/Restrict-связи и границу локальных Identity-полей. `SharedStorageMigrationTests` сравнивает snapshot и design-time model через `IMigrationsModelDiffer`, проверяет операции Up/Down без SQL execution. `SharedStorageRegistrationTests` разрешает storage DI и проверяет закрытое соединение; workers не стартуют.

`ProjectionTransactionTests` вызывает реальный `ProjectionApplyUnitOfWork` с fake repository/session/transaction: целый пакет передаётся в staging, затем save/commit; повтор не staging-ит заново; ошибка приводит к rollback/clear и допускает повтор; другой digest отвергается. Подставное хранилище не доказывает атомарность аккаунт + роль + cursor на SQL Server, реальные constraints или crash recovery.

Регрессионный staging-тест в `SharedStorageModelTests` воспроизвёл повторное создание удалённого NodeId из старой tracked-копии: до исправления ожидание отказа падало, после общей проверки tombstone для member/observer assignment/node chat route проходит. Данные взяты из tracker; соединение не открывается, SQL не выполняется.

Не запускались полный Infrastructure-набор, provider tests (включая SQLite), Web/frontend tests, применение миграций, БД/SQL, приложение, Docker, реальные HTTP/Telegram или deployment. Прежние provider-тесты и старые permission use case частично сохранены как совместимость до 05; их наличие не означает готовность общего входа. Runtime SQL Server, Raft WAL/replay, восстановление процесса и распределённая доставка не проверены. Модель и ограничения — [Shared cluster storage.md](Shared%20cluster%20storage.md).

## Изоляция проверок

Обычные тесты не обращаются к реальной БД, HTTP endpoint, Docker, PowerShell, процессам или внешним сервисам.

EF metadata tests не открывают соединение. Provider-level concurrency/outbox/unique tests используют только SQLite in-memory connection внутри процесса, без SQL Server, SQL-команд и Docker. SQLite подтверждает ограничения модели и контролируемые interleaving, но не доказывает SQL Server error numbers, lock granularity, `READ_COMMITTED_SNAPSHOT`, deadlock behavior или migration runtime. HTTP-тесты monitoring adapter используют управляемый fake handler и не создают сетевых соединений. Worker-тесты используют in-memory services и управляемые `TaskCompletionSource`.

Каждый test-файл явно импортирует `Xunit`; `GlobalUsings.cs` в проекте не используется.

SignalR-тесты используют fake `IHubContext` и не открывают WebSocket. Frontend lifecycle проверяется статически по TypeScript-источнику; реальный Web-хост и браузер в изолированном наборе не запускаются. Поэтому фактический browser-сценарий «30 быстрых событий → ограниченное число snapshot requests», реальная отмена fetch и настоящий MVC input formatter pipeline остаются ручными runtime-проверками и не считаются доказанными статическими тестами.

Web security tests статически проверяют отсутствие CDN в `Index.cshtml`, локальное подключение SignalR с versioning, наличие и SHA-256 asset версии 10.0.11, а также фактическое добавление CSP middleware и отсутствие запрещённых директив/origins. Реальная browser/runtime-проверка SignalR и CSP остаётся ручной и в изолированный набор не включается.

Configuration contract tests не запускают Web-host и поэтому не доказывают runtime-загрузку внешнего `Config/config.json`; отсутствие/ошибка JSON обеспечиваются параметрами обязательного configuration source и остаются частью будущей ручной проверки запуска.

Проверка доработок modal редактора объектов, полей поиска, переключателей и Telegram: Web и затронутые test-проекты собраны без предупреждений/ошибок; 15 transport-тестов, 16 Application notification administration tests, 41 выбранный Web-тест и 3 frontend-теста ресайза прошли. Изолированные проверки не подтверждают внешний вид/фокус в настоящем браузере и реальную доставку Telegram; приложение, БД и внешняя сеть не запускались.

## Проверка этапа 04 и зависимости 05.1

2026-10-29 ранний 05.1 и 04.1–04.5 проверены в согласованном минимальном изолированном объёме. Четыре production-проекта собраны отдельно в Debug, `--no-restore`, с раздельным `BaseOutputPath=...\artifacts\compile-check\<project>\`: 0 предупреждений и ошибок. Web MSBuild скомпилировал существующий TypeScript target; Web-host не запускался.

| Набор | Успешно | Ошибки | Пропуски |
| --- | --- | --- | --- |
| Domain, весь изолированный проект | 36 | 0 | 0 |
| Application, весь изолированный проект | 166 | 0 | 0 |
| Infrastructure, только выбранные ниже классы | 65 | 0 | 0 |
| Web, только Cluster и DependencyInjection | 22 | 0 | 0 |

Фактические команды из корня репозитория:

```powershell
dotnet test --project .\tests\UptimeHarbor.Domain.Tests\UptimeHarbor.Domain.Tests.csproj -c Debug --no-restore -p:BaseOutputPath=D:\Media\User\source\repos\uptime-harbor\artifacts\compile-check\domain-tests\
dotnet test --project .\tests\UptimeHarbor.Application.Tests\UptimeHarbor.Application.Tests.csproj -c Debug --no-restore -p:BaseOutputPath=D:\Media\User\source\repos\uptime-harbor\artifacts\compile-check\application-tests\
dotnet test --project .\tests\UptimeHarbor.Infrastructure.Tests\UptimeHarbor.Infrastructure.Tests.csproj -c Debug --no-restore -p:BaseOutputPath=D:\Media\User\source\repos\uptime-harbor\artifacts\compile-check\infrastructure-tests\ -- --filter-class UptimeHarbor.Tests.Infrastructure.Cluster.* --filter-class UptimeHarbor.Tests.Infrastructure.Persistence.ProjectionTransactionTests --filter-class UptimeHarbor.Tests.Infrastructure.Persistence.SharedStorageModelTests --filter-class UptimeHarbor.Tests.Infrastructure.Persistence.SharedStorageMigrationTests --filter-class UptimeHarbor.Tests.Infrastructure.Persistence.SharedStorageRegistrationTests
dotnet test --project .\tests\UptimeHarbor.Web.Tests\UptimeHarbor.Web.Tests.csproj -c Debug --no-restore -p:BaseOutputPath=D:\Media\User\source\repos\uptime-harbor\artifacts\compile-check\web-tests\ -- --filter-class UptimeHarbor.Tests.Web.Cluster.* --filter-class UptimeHarbor.Tests.Web.DependencyInjection.*
```

`AcceptedAdministratorTests` проверяет anonymous/disabled/deleted/unaccepted и принятый активный аккаунт; membership/test-send tests отключают аккаунт между первой и второй policy-проверкой. Claims роли/активности не подменяют accepted state.

`PendingCommandTests` проверяет trusted identity, защищённый payload через изолированный protection double, restart/retry с исходной actor revision, потерянный accepted response, old backup reconciliation, held replacement и отказ stale/disabled confirmation. Capacity tests проверяют лимит, резерв controls, допустимость retry и конкурентные scopes с общим admission gate. `RevisionReducerTests` вызывает production reducer/write-set: dedup, cancel/replace race/late original, смена term, конфликты и разрешение при обратном ходе календарных часов, уникальность login/server name/chat ID, tombstones/родитель, deleted login, credential stamp и endpoint generation.

`ProjectionTransactionTests` вызывает production reducer + apply engine + ProjectionApplyUnitOfWork с fake repository/session/transaction, моделируя отказ до save и потерянный ответ после commit, повтор apply и duplicate OperationId в новом term. Это проверка сценарной транзакционной границы, а не SQL Server atomicity/locking/constraints.

Проверены размеры новых cluster-файлов: каждый меньше 20000 байт. Существующие `ObjectAdministrationService.cs` (25435 байт) и его tests (31197 байт) оставлены цельными: изменения ограничены удалением ACL cleanup/обновлением ожиданий, новая ответственность туда не добавлялась. Документальные ссылки, solution items и UTF-8 проверены без проектных скриптов; 81 Markdown-документ без битых ссылок, все documentation solution items присутствуют. Смешанные окончания строк встречаются в существующих файлах; массовая нормализация вне точечных правок не выполнялась.

`RevisionRecoveryTests` использует настоящий DotNext WAL во временном каталоге: snapshot → append/commit → dispose/reopen → production replay; accepted accounts закрыты до подтверждения committed/applied/membership/SQL cut. Projection подставная: нет доказательства SQL replay на provider, majority/elections, power-loss или crash/fsync. Отдельно проверены unknown admission и checkpoint failure без смены опубликованного snapshot.

`RevisionSnapshotTests` проверяет manifest/hash/compatibility, размер chunk receiver, bounded streaming import/overflow/temp cleanup, compaction с controls/decisions/tombstone и late original, whitelist локальных полей и stale credential rows. Это не выполнение SQL snapshot install: сохранность локальных rows и один outer commit подтверждены кодом/границами, provider execution остаётся непроверенным.

`RevisionBatchTests`/`PendingReplicationTests` проверяют malformed tail/response, дубли/перестановки/пропуски, фактические count/byte limits, минутный цикл, priority retry, backlog/held backup без payload. `RevisionBoundaryTests` вызывает controller без host и проверяет отсутствие dispatch при неверном peer/batch. Migration tests проверяют только operations/model metadata, не SQL DDL.

Не запускались БД/SQL (включая SQLite), полный Infrastructure-набор, provider tests, приложение/hosting, Docker, реальные peer HTTP/TLS/Nginx, мониторинг/Telegram, процессы, deployment и Git. Общий sign-in/сессии 05.2–05.4, распределённые incidents/delivery 06/07, UI/controlled enable 08/09 не реализовывались; закрытые зависимости остаются закрытыми. Часовая цель догона трёх узлов/около 30 приложений не наблюдалась и не доказана. Самостоятельная внешняя приёмка учитывается отдельно; полного стенда и прогона всей матрицы не было.

## Проверка этапа 05

Статическая финальная проверка: 81 Markdown-документ без битых файловых ссылок, все documentation solution items существуют и включены; новые документы не добавлялись. 96 новых/изменённых файлов проверены строгим UTF-8 decoder, без U+FFFD, последовательностей из четырёх знаков вопроса и обнаруженных признаков mojibake. Новые/изменённые `.cs` меньше 20000 байт. Существующие смешанные окончания строк не нормализовались массово; несвязанные файлы и история 01–04/05.1 не менялись.

Регрессия recovery-version сначала воспроизвела дефект: успешная проверка, начатая до нового Invalidate, повторно открывала reader. Production store/apply теперь сравнивают RecoveryVersion начала проверки под общей границей публикации; устаревший успех отвергается. Локальный session epoch включает эту версию и не зависит от порядка доставки Changed. Финальный AccountSessionTests включает эту проверку.

2026-10-29 выполнен минимальный изолированный объём аккаунтной интеграции и регрессий production pipeline 04. Четыре production-проекта собраны **последовательно и отдельно** в Debug без restore: 0 предупреждений и ошибок. Общие obj не позволяют безопасно выполнять эти сборки/тестовые builds одновременно. Использованы команды:

```powershell
dotnet build .\UptimeHarbor.Domain\UptimeHarbor.Domain.csproj -c Debug --no-restore -p:BaseOutputPath=D:\Media\User\source\repos\uptime-harbor\artifacts\compile-check\domain\
dotnet build .\UptimeHarbor.Application\UptimeHarbor.Application.csproj -c Debug --no-restore -p:BaseOutputPath=D:\Media\User\source\repos\uptime-harbor\artifacts\compile-check\application\
dotnet build .\UptimeHarbor.Infrastructure\UptimeHarbor.Infrastructure.csproj -c Debug --no-restore -p:BaseOutputPath=D:\Media\User\source\repos\uptime-harbor\artifacts\compile-check\infrastructure\
dotnet build .\UptimeHarbor.Web\UptimeHarbor.Web.csproj -c Debug --no-restore -p:BaseOutputPath=D:\Media\User\source\repos\uptime-harbor\artifacts\compile-check\web\
```

| Набор | Успешно | Ошибки | Пропуски |
| --- | --- | --- | --- |
| Domain, весь изолированный проект | 36 | 0 | 0 |
| Application, весь изолированный проект | 172 | 0 | 0 |
| Infrastructure, только перечисленные классы | 82 | 0 | 0 |
| Web, только перечисленные классы | 50 | 0 | 0 |

Фактические финальные test-команды из корня:

```powershell
dotnet test --project .\tests\UptimeHarbor.Domain.Tests\UptimeHarbor.Domain.Tests.csproj -c Debug --no-restore -p:BaseOutputPath=D:\Media\User\source\repos\uptime-harbor\artifacts\compile-check\domain-tests\
dotnet test --project .\tests\UptimeHarbor.Application.Tests\UptimeHarbor.Application.Tests.csproj -c Debug --no-restore -p:BaseOutputPath=D:\Media\User\source\repos\uptime-harbor\artifacts\compile-check\application-tests\
dotnet test --project .\tests\UptimeHarbor.Infrastructure.Tests\UptimeHarbor.Infrastructure.Tests.csproj -c Debug --no-restore -p:BaseOutputPath=D:\Media\User\source\repos\uptime-harbor\artifacts\compile-check\infrastructure-tests\ -- --filter-class UptimeHarbor.Tests.Infrastructure.Identity.AcceptedSignInTests --filter-class UptimeHarbor.Tests.Infrastructure.Identity.AcceptedCookieTests --filter-class UptimeHarbor.Tests.Infrastructure.Identity.AccountBootstrapTests --filter-class UptimeHarbor.Tests.Infrastructure.Cluster.* --filter-class UptimeHarbor.Tests.Infrastructure.Persistence.ProjectionTransactionTests --filter-class UptimeHarbor.Tests.Infrastructure.Persistence.SharedStorageModelTests --filter-class UptimeHarbor.Tests.Infrastructure.Persistence.SharedStorageMigrationTests --filter-class UptimeHarbor.Tests.Infrastructure.Persistence.SharedStorageRegistrationTests
dotnet test --project .\tests\UptimeHarbor.Web.Tests\UptimeHarbor.Web.Tests.csproj -c Debug --no-restore -p:BaseOutputPath=D:\Media\User\source\repos\uptime-harbor\artifacts\compile-check\web-tests\ -- --filter-class UptimeHarbor.Tests.Web.Cluster.* --filter-class UptimeHarbor.Tests.Web.DependencyInjection.* --filter-class UptimeHarbor.Tests.Web.AdminUsersControllerAuthorizationTests --filter-class UptimeHarbor.Tests.Web.ReadContractSecurityTests --filter-class UptimeHarbor.Tests.Web.WebPerimeterSecurityTests --filter-class UptimeHarbor.Tests.Web.DashboardSignalRTests
```

AcceptedSignInTests вызывает реальные password validators/hasher, AcceptedUserSignInService, LocalAccountLogin и session policy с изолированными UoW/authentication/time. Проверены общий credential на двух локальных экземплярах, локальные lockout/cookie, inactive/unaccepted/recovery deny, ordinary offline и apply смены пароля, отсутствие rehash/journal записи каждого входа, отказ при apply во время проверки/выдачи cookie и отсутствие секретов/ложного success в проверяемых логах. AcceptedCookieTests вызывает фактически зарегистрированный OnValidatePrincipal и проверяет NodeId discriminator без чтения key repository или SQL.

SharedUserManagementTests использует production Pending/reducer с изолированными storage/protection/credential doubles: Create/Update/Password/SetEnabled/Delete, Pending без изменения входа, повтор OperationId/потерянный ответ, concurrent normalized login, deactivation последних администраторов/повторная авторизация, self/stale/changed retry и удержание restore Pending. Safe DTO не содержит credentials. PendingReplicationTests проверяет однократный подтверждённый account audit с точными operation/actor/subject/node IDs. Прежние reducer/transaction/batch/snapshot tests 04 включены повторно.

AccountBootstrapTests проверяет точный trusted digest, единственного первого Administrator, отказ Join/Restart/Disabled/old-backup и dedup. AdapterWiringTests вызывает настоящий DotNext replication API с локальным WAL и подменённой transport boundary; узкий bootstrap проходит при закрытом общем gate, другая команда/fault — нет. Это не native majority/elections.

RevisionRecoveryTests использует реальный локальный snapshot/WAL и production recovery/reducer: старый snapshot → новые credentials/удаление → reopen/replay, аккаунты закрыты до подтверждения, прежняя cookie остаётся недействительной, tombstone сохраняется. Проекция подставная; SQL recovery/atomicity/locking и power-loss/crash-fsync этим не доказаны. AccountSessionTests вызывает production apply/session/connection registry с fake HubCallerContext, проверяя отзыв password/stamp/disable/delete/fault и сохранность обычной offline-сессии/периодической сверки. Это не browser/WebSocket acceptance.

Новая migration не нужна; существующие designer/snapshot/historical migrations не изменялись. БД/SQL (включая SQLite), provider tests, полный Infrastructure-набор, приложение/hosting, Docker, реальные peers/monitoring/Telegram и deployment не запускались; Git-команды не выполнялись. Схема к БД не применялась. UI этапа 08, provisioning/внешнее fencing/controlled enable 09 и распределённые workers 06/07 не реализовывались. Все соответствующие gates и неизвестные proof остаются закрытыми. Этапы 06–10 не начаты; внешняя приёмка и часовая цель догона не подтверждены.
