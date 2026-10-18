# Локальные библиотеки

## Назначение и граница

`libs/EFCoreLibrary.dll` и `libs/HttpClientLibrary.dll` подключены файловыми `Reference` только в [Infrastructure.csproj](../../UptimeHarbor.Infrastructure/UptimeHarbor.Infrastructure.csproj). Domain и Application от них не зависят. Соседние исходники не подключаются через `ProjectReference` и не включаются в решение.

EFCoreLibrary предоставляет базовые EF repositories и scoped adapter. Прикладные ports и сценарные Unit of Work остаются собственными контрактами UptimeHarbor. HttpClientLibrary регистрируется как typed `IHttpApiClient`/`HttpApiClient`; monitoring checker и Telegram sender используют собственные HTTP-адаптеры приложения. Обновление DLL не означает перевод этих адаптеров на другой транспорт.

## Источники и текущая поставка

Поставка обновлена 18 октября 2026 года отдельной Debug-сборкой `net10.0` из:

- `D:\Media\User\source\repos\work\EFCoreLibrary\EFCoreLibrary.csproj`;
- `D:\Media\User\source\repos\work\HttpClientLibrary\HttpClientLibrary.csproj`.

| Файл | Assembly version | File version | Обязательные зависимости исходного проекта |
| --- | --- | --- | --- |
| `EFCoreLibrary.dll` | `0.0.5.0` | `0.0.5.0` | EF Core и DI.Abstractions `10.0.12` |
| `HttpClientLibrary.dll` | `1.0.0.0` | `0.0.0.5` | Logging.Abstractions `10.0.12` для `net10.0` |

Рядом с HttpClientLibrary поставляется соответствующий `HttpClientLibrary.xml`. Сам исходный HTTP-проект также поддерживает `net8.0`, но его net8-поставка в UptimeHarbor не используется и в этой задаче не собиралась. Optional maintenance-модули EFCoreLibrary не поставляются и не подключаются.

SHA-256 поставленных DLL:

```text
EFCoreLibrary.dll
4046A05F1930F7BF5D131657EE0E8CF83AA7B33E6090145D9FA4CAA342995D26

HttpClientLibrary.dll
33CD0E23F9A707B7EB6AC1461A26B564419FFCFACA57E714262EC58CE4421BAF
```

Хеши идентифицируют эту поставку, а не являются постоянными значениями для будущих сборок. Изменения исходников соседних репозиториев сами по себе не обновляют `libs`.

## Совместимость потребителя

Файловый `Reference` не переносит NuGet-зависимости библиотеки. Поэтому EF Core/Identity/Data Protection packages в Infrastructure, EF Design в Web и DI packages в Application и его тестах согласованы на `10.0.12`. SQLite package в инфраструктурных тестах также имеет версию `10.0.12`. Версия локального SignalR browser asset `10.0.11` относится к отдельной поставке и не меняется вместе с EF DLL.

Новый EF API несовместим с прежними двухпараметрическими CRUD-интерфейсами потребителя:

- `AddEfCoreContext<UptimeHarborDbContext, UptimeHarborDbContext>()` регистрирует `IAppDbContext<UptimeHarborDbContext>` через `EfDbContextAdapter<UptimeHarborDbContext, UptimeHarborDbContext>`;
- `AddEfCoreBaseRepositories<UptimeHarborDbContext>()` регистрирует `IContext*Repository`;
- предметные репозитории внедряют `IContextGetItemByPredicateRepository<TEntity, UptimeHarborDbContext>`, `IContextCreateItemRepository<TEntity, UptimeHarborDbContext>` и `IContextDeleteItemRepository<TEntity, UptimeHarborDbContext>`.

Один scoped `UptimeHarborDbContext` используется и adapter, и сценарными Unit of Work. Его тип здесь служит также ключом контекста. Чистые Application-интерфейсы и семантика tracked/no-tracking запросов сохранены. Подробности транзакций и ожидаемых persistence ошибок находятся в [Persistence.md](Persistence.md).

## Порядок обновления

1. Прочитать инструкции исходных библиотек, их project/build-файлы, TFM и зависимости; не выбирать DLL по одной дате изменения файла.
2. Собрать конкретные проекты без pack/publish и запуска приложения. Для EFCoreLibrary отключить `GeneratePackageOnBuild`, для HttpClientLibrary явно выбрать `net10.0`. Использовать отдельный output в `artifacts/library-refresh`.
3. Скопировать только библиотечные DLL и соответствующую XML-документацию в `libs`. Не переносить весь output и optional-модули.
4. Согласовать NuGet graph потребителя и реальные API/DI registrations. Не добавлять инфраструктурные зависимости в Domain/Application.
5. Отдельно собрать production-проекты и test-проекты, затем выполнить разрешённые изолированные тесты. Сверить identity/хеши, обновить этот документ и ближайшие `AGENTS.md`.

Команды сборки библиотек, выполненные для этой поставки из корня UptimeHarbor:

```powershell
dotnet build 'D:\Media\User\source\repos\work\EFCoreLibrary\EFCoreLibrary.csproj' -c Debug --no-restore -p:GeneratePackageOnBuild=false -p:BaseOutputPath='D:\Media\User\source\repos\uptime-harbor\artifacts\library-refresh\EFCoreLibrary\'
dotnet build 'D:\Media\User\source\repos\work\HttpClientLibrary\HttpClientLibrary.csproj' -c Debug -f net10.0 --no-restore -p:GeneratePackageOnBuild=false -p:BaseOutputPath='D:\Media\User\source\repos\uptime-harbor\artifacts\library-refresh\HttpClientLibrary\'
```

## Проверка 18 октября 2026 года

Обе библиотеки собраны без ошибок и предупреждений. Четыре production-проекта и четыре test-проекта UptimeHarbor отдельно собраны в Debug; Web DI validation разрешает сценарные UoW и новый repository graph без подключения к БД.

| Набор | Объём | Пройдено | Упало | Пропущено внутри выбранного набора |
| --- | --- | --- | --- | --- |
| Domain | Весь проект | 19 | 0 | 0 |
| Application | Весь проект | 98 | 0 | 0 |
| Infrastructure | Только шесть классов без БД, перечисленных в [Testing.md](Testing.md) | 56 | 0 | 0 |
| Web | Весь проект, без запуска host | 68 | 0 | 0 |

Всего пройден 241 тест. Первый Web-прогон с общим output завершился 18 ошибками поиска исходников из-за неверной глубины каталога; после выбора `artifacts/compile-check/web-tests/Debug/net10.0` весь набор прошёл без изменения тестового кода. Первоначальный query-фильтр Infrastructure не выбрал тесты; итоговый набор выполнен с явными `--filter-class`.

NuGet выдаёт `NU1902`/`NU1903` по транзитивным `System.Security.Cryptography.Pkcs` (`9.0.11` в production graph и `10.0.12` в test graph) и `System.Security.Cryptography.Xml` (`10.0.12` в test graph). Эти предупреждения не скрывались; обновление транзитивных security packages не входит в замену двух локальных DLL.

Тесты SQLite, самостоятельные test-наборы исходных библиотек, SQL Server, миграции/backup, реальный HTTP/Telegram, Web-host, браузер, Docker и deployment в этой задаче не запускались. Сборка и DI validation подтверждают совместимость проверенного потребителя; внешняя приёмка остаётся открытой.
