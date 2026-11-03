# Конфигурация и запуск

## Совместимость приглашений (2026-11-03)

Новое API требует согласованного набора ProtocolMajor/CommandSchema/SnapshotFormat/SqlSchema = 2, SerializerVersion = 1, DotNext 6.10.0 (`2.0/2/2/2/1/6.10.0`). Validator сохраняет прежний набор v1; он не принимает новые onboarding command/snapshot. Defaults, config.example.json и установка не менялись. Схему и все voters нужно подготовить отдельно; одна смена версии не даёт права подключения. Production IEnrollmentAuthority всегда отказывает, новый native Join/activation закрыт. Сроки и лимиты описаны в [Onboarding contracts.md](Onboarding%20contracts.md).

## Локальное управление (09)

`ClusterControl` в config.example.json содержит Directory, PublicKeyPath, PublicKeyDigest (uppercase SHA256 SPKI) и отдельный KeyRingDirectory для защищённой bootstrap-команды. Пути абсолютные; приватный operator key вне узлов, node/Telegram secrets локальны и не реплицируются. Safe status/challenge публикуются в control directory даже при закрытом browser. Пустая/неверная конфигурация или отсутствующее доказательство не открывает gate. Default Disabled сохранён. [Форматы и точные команды](Cluster%20control%20files.md), [первый запуск/сертификаты/обслуживание](Cluster%20operations.md).

## Интерфейс сетки (08)

ClusterOptions.Enabled выбирает общий cluster UI; default Mode=Disabled сохранён. DI регистрирует один ClusterHealthWorker для hosted polling и diagnostics query. Сборка Razor/TypeScript использует существующий Web target; hosting и runtime не требуются для compile-check. UI не открывает admission/trust/activation/manual-enable/sender/delivery gates; controlled enable реализован lifecycle 09 и требует настоящих подтверждений.


## Настройки доставки этапа 07

Новых operational settings и секретов не добавлено. BotToken остаётся локальным TelegramOptions; transport использует существующий HTTP timeout 30 секунд. Retry ограничен пятью attempts и учитывает безопасный retry_after. Authority renewal выполняется примерно раз в 5 секунд при сроке 10 секунд и drain 12 секунд.

Cluster:Mode по умолчанию Disabled. Наличие sender-компонентов не подтверждает admission, trust, activation, manual enable, sender epoch или delivery readiness; эти зависимости остаются закрытыми до controlled enable 09. Файлы Config/config.json и его секреты не читались/не изменялись. [Контракт](Notifications.md#принятая-доставка-этапа-07).

## Настройки принятого мониторинга (06)

Назначенные ObserverNodes, FailureThreshold (1–100), IntervalSeconds (5–3600), TimeoutSeconds (1–60), CheckGeneration и ConfigRevision относятся к принятой конфигурации server/container. Это не локальные appsettings overrides. Их изменение проходит общий Administrator pipeline; смена endpoint/enable/observers/порога/периодов требует нового поколения. По умолчанию — 3 ошибки, 60/10 секунд; пустой назначенный набор не запускает проверки.

MonitoringNodes (NodeId → storage generation) приходит из trusted RecoveryAdmission и сверяется с snapshot/ClusterMember; публичное config поле для неподтверждённого допуска не добавлено. Node checks используют отдельный peer health, 5/3 секунд и шесть сетевых ошибок. Default Cluster Mode остаётся Disabled, provisioning/activation — 09; схема и секреты не менялись.

## Источники настроек

После `WebApplication.CreateBuilder` Web-проект использует стандартные источники ASP.NET Core, а затем добавляет обязательный `Config/config.json` относительно content root с `optional: false` и `reloadOnChange: false`. Поэтому этот файл имеет приоритет над ранее добавленными стандартными источниками и должен существовать до старта. Отсутствующий файл или некорректный JSON останавливают запуск до регистрации прикладных и инфраструктурных сервисов.

Назначение файлов разделено:

- `appsettings.json` содержит только секцию `Serilog`;
- `appsettings.Development.json` содержит только нечувствительные development-only настройки, сейчас `DetailedErrors`;
- `Config/config.json` содержит operational/security settings и секреты, не отслеживается git и не копируется в build output или publish;
- `Config/config.example.json` — отслеживаемый полный безопасный пример с placeholders, который приложение никогда не загружает.

Для локального или опубликованного запуска нужно скопировать `Config/config.example.json` в `Config/config.json`, затем заменить bootstrap login/password, пароль SQL Server, Telegram bot token и при необходимости proxy CIDR/`AllowedHosts`. При publish файл не упаковывается: окружение запуска должно предоставить его снаружи по пути `Config/config.json` относительно content root.

В `Config/config.json` находятся секции `BootstrapAdmin`, `ConnectionStrings:SqlServer`, `Monitoring`, `Telegram`, `Cluster`, `ClusterControl`, `ForwardedHeaders` и `AllowedHosts`. При будущем включении сетки отдельно нужны `Kestrel:Endpoints:Browser/Peer`. В репозитории нет настоящих значений секретов; локальный секретный файл в этапе 03 не изменялся.

## Типизированные группы

Определены пять options-типов:

- `BootstrapAdminOptions` — логин и начальный пароль администратора;
- `MonitoringOptions` — принадлежащий Application общий контракт интервала, timeout и порога ошибок;
- `TelegramOptions` — принадлежащий Infrastructure тип с токеном бота.
- `ClusterOptions` — режим, identity/storage generation, canonical HTTPS URI, mTLS boundary и пути, таймеры, compatibility и пределы; привязка/валидация выполняется в `AddClusterInfrastructure`, настройка listener — в Web extension.
- `ClusterControlOptions` — пути локальных control-файлов, pinned публичного ключа и отдельного bootstrap keyring; привязка выполняется в AddClusterInfrastructure.

`MonitoringOptions` и `TelegramOptions` привязываются в Web через `AddApplicationConfiguration` и используют `ValidateOnStart`. Monitoring worker и HTTP-адаптер получают только `IOptions<MonitoringOptions>`, Telegram adapter — только `IOptions<TelegramOptions>`; потребители не зависят от `IConfiguration`. `BootstrapAdminOptions` принадлежит Infrastructure и валидируется при bootstrap только если администратор отсутствует. Это позволяет повторному запуску с уже созданным администратором не зависеть от сохранения начального пароля в конфигурации.

Строка подключения `ConnectionStrings:SqlServer` проверяется в `AddInfrastructure` до регистрации DbContext. Она поступает из обязательного внешнего `Config/config.json`; секреты не хранятся в репозитории или publish-артефакте.

`ForwardedHeaders:KnownNetworks` задаёт CIDR доверенной proxy-сети; приложение принимает forwarded-заголовки только из этой сети и ограничивает цепочку одним прокси (`ForwardLimit = 1`). Значение сети должно совпадать с подсетью используемого reverse proxy.

## Настройки сетки этапов 03/04

Безопасный пример содержит `Cluster:Mode=Disabled`. Режимы `Bootstrap`, `Join`, `Restart` требуют ненулевые UUID identity/credential/storage, generation >= 1, отдельный provisioned StoragePath, абсолютные пути сертификатов/ключа/CA, SHA-256 CA fingerprint и ожидаемый общий BotId. PublicUri — canonical `https://<peer-host>/internal/raft`. Состав/доверие приходят из принятого registry, а не из списка доступных адресов. Секретный CA key на узле не нужен. Таймеры/версии протокола проверяются строго; лимиты могут только уменьшаться в согласованных пределах. Неподтверждённые accepted state и recovery запрещают старт даже с корректными options.

Enabled-режим требует два именованных Kestrel endpoint с разными портами. Пример структуры прямого TLS (структура не заменяет controlled enable):

```json
"Kestrel": {
  "Endpoints": {
    "Browser": { "Url": "http://127.0.0.1:5000" },
    "Peer": {
      "Url": "https://0.0.0.0:8443",
      "Certificate": { "Path": "/srv/uptime/keys/server.crt", "KeyPath": "/srv/uptime/keys/server.key" }
    }
  }
}
```

Direct listener требует client certificate и проверяет private CA; middleware дополнительно проверяет accepted pin/SAN/generation. Для `NginxIsolatedHttp` Peer.Url должен быть внутренним HTTP на PeerListenPort, `TrustedProxyAddresses` содержит точные IP. Firewall запрещает обход Nginx; proxy проверяет mTLS/CRL, перезаписывает `Ssl-Client-Verify` и URL-encoded публичный `Ssl-Client-Cert`, сохраняет peer Host и отключает early data. Публичные TLS DNS/certificate/renewal и изоляция проверяются оператором отдельно. В [Cluster runtime.md](Cluster%20runtime.md) приведены lifecycle, лимиты и границы будущих этапов. Enabled не запускает прежние workers/startup migration. Пользовательские маршруты и отправка закрыты до controlled enable и подтверждения всех соответствующих gates.

`ConfigurationIntervalSeconds` — 60–3600, кратно 60; обычные настройки передаются минутным циклом. Account/delete/control проходят priority pump с шагом 1 с и retry 1/2/5/10/30 с плюс jitter ≤20%. `SnapshotIntervalSeconds` — 60–3600, по умолчанию 300; без подтверждённого recovery/catch-up snapshot worker ничего не публикует.

`CommandMaxBytes` ≤256 KiB; `BatchMaxEntries` ≤100, `BatchMaxBytes` ≤1 MiB; `SnapshotChunkMaxBytes` 16 KiB–1 MiB. Snapshot ограничен 64 MiB с резервом 64 KiB под native envelope и не превышает `NativeMaxBytes`. Receiver использует размер chunk из manifest. Приём Pending ограничен 10000 незавершённых записей (1000 зарезервировано для controls), singleton admission gate сериализует scopes одного NodeId. Это фиксированный предел, отдельной option нет. Очередь имеет bounded pages/packets; снижение лимита ниже размера головной заявки даёт безопасный `BatchCapacityLimit`, не молчаливую потерю. Эти параметры не доказывают производительность и часовую цель догона.

## Проверка DI

В Development включены `ValidateScopes` и `ValidateOnBuild`. Это обнаруживает captive dependencies и недоступные регистрации при построении контейнера.

## Логирование

Web использует настоящий Serilog через `builder.Host.UseSerilog` и `ReadFrom.Configuration(builder.Configuration)`. Секция `Serilog` задаёт уровень `Information` по умолчанию, warning overrides платформы, `FromLogContext` для scopes и два sinks с единым человекочитаемым UTC-шаблоном:

- Console выводит строки вида `[UTC-время] [уровень][source context] - сообщение {structured properties}`;
- File пишет человекочитаемые строки вида `[UTC-время] [уровень][source context] - сообщение {structured properties}` в `Logs/log_*.log`, переключает файл ежедневно и при достижении 10 MiB, хранит не более 31 файла. Исключение выводится следующей строкой со stack trace.

`Logs` расположен вне `wwwroot`, игнорируется git и исключён через `DefaultItemExcludes` из build/publish. При локальном запуске каталог создаёт File sink с правами процесса. В container `/app/Logs` имеет режим `0700` и владельца non-root app user; Docker volume сохраняет файлы между пересозданиями контейнера. При bind mount оператор обязан настроить совместимые owner/permissions на host.

Connection string, пароли, Telegram token, cookies, request/response body и полный Telegram URI запрещено передавать в сообщения, properties и scopes: File sink получает те же события, что и Console, и не является дополнительным фильтром. Serilog request-logging middleware не включён. Стандартное HTTP-логирование Telegram-клиента по-прежнему удалено, чтобы token из URI не попадал в логи.

## Время

Application регистрирует `TimeProvider.System`. Сценарии, зависящие от текущего времени, должны получать `TimeProvider` через DI, а не обращаться напрямую к системным часам.

## Telegram-секрет

Используется один bot token. Он не передаётся в HTML, TypeScript или административный API. Telegram Bot API включает token в URL, поэтому стандартное HTTP-логирование этого клиента отключено; adapter не включает URI, тело ответа или текст сообщения в исключения.

`TelegramOptions.HasValidBotToken` проверяет формат: положительный числовой ID бота, двоеточие и секрет длиной 30–128 символов из ASCII-букв, цифр, `_` и `-`. `ValidateOnStart` отклоняет пустое значение, обычный ID вместо токена и заглушку из `config.example.json`. Ошибка указывает на `Telegram:BotToken` и `Config/config.json`, не выводя значение. Для работы нужно заменить заглушку токеном от BotFather и перезапустить приложение. Проверка формата не доказывает, что токен действующий: отклонённый или отозванный токен определяется ответом Telegram при отправке.

## Локальные credentials и сессии в сетке (05)

Enabled-регистрации выбирают accepted sign-in/account store; ключи Data Protection остаются в локальной SQL таблице и исключены из shared snapshot. Application discriminator имеет вид `UptimeHarbor/{NodeId:N}`. Cookie дополнительно привязана к случайному process epoch, поэтому копия старой БД/ключей не возвращает старую сессию после restart. Lockout — пять ошибок/пять минут на одном узле; сброс/rehash не передаётся общим журналом.

`BootstrapAdmin` используется только однопроцессным Disabled startup. Для аккаунтного cluster bootstrap нужны точная защищённая prepared команда и подписанный RecoveryAdmission; ни Mode=Bootstrap, ни секрет в config не создают proof. Provisioning/control и ручное подтверждение изоляции реализованы в 09; публичного bootstrap endpoint нет. Default Mode остаётся Disabled.
