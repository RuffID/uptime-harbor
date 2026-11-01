# Подготовка Ubuntu-сервера к мониторингу

Это настройка проверяемого Ubuntu-сервера без контейнеров. UptimeHarbor обращается к nginx и получает внутреннее имя машины и время загрузки ОС в UTC:

```json
{"name":"ubuntu-server-01","startedAtUtc":"2026-11-01T03:20:00Z"}
```

`Z` означает UTC, то есть часовой пояс `+00:00`. Часовой пояс самой Ubuntu менять не требуется. Её часы и часы машины UptimeHarbor должны быть синхронизированы.

Endpoint открыт для всех, без авторизации и ограничений по IP. UFW не настраивается. Доступность порта со стороны хостинга или роутера обеспечивается отдельно, если там есть ограничения.

Команды ниже выполняются на проверяемой Ubuntu оператором. В рамках изменения проекта они не запускались; работоспособность настройки в реальном Ubuntu/nginx ещё не проверена. До запуска обновлённого UptimeHarbor нужна подготовка схемы из [Database migrations.md](Database%20migrations.md#время-загрузки-сервера).

## 1. Установить необходимые пакеты

```bash
sudo apt update
sudo apt install nginx jq curl
```

Используется отдельный порт `8088`, который должен быть свободен. Существующие сайты на `80` и `443` менять не требуется.

## 2. Создать скрипт формирования JSON

Открыть файл:

```bash
sudo nano /usr/local/sbin/uptime-harbor-health.sh
```

Вставить:

```sh
#!/bin/sh
set -eu
export LC_ALL=C

boot_epoch=$(awk '$1 == "btime" { print $2 }' /proc/stat)
started_at_utc=$(date -u -d "@${boot_epoch}" '+%Y-%m-%dT%H:%M:%SZ')
server_name=$(hostname)

jq -n \
    --arg name "$server_name" \
    --arg startedAtUtc "$started_at_utc" \
    '{name: $name, startedAtUtc: $startedAtUtc}' \
    > /run/uptime-harbor-health/health.json.tmp

chmod 644 /run/uptime-harbor-health/health.json.tmp
mv -f /run/uptime-harbor-health/health.json.tmp /run/uptime-harbor-health/health.json
```

Сохранить: Ctrl+O, Enter, Ctrl+X. Разрешить выполнение:

```bash
sudo chmod 755 /usr/local/sbin/uptime-harbor-health.sh
```

`btime` в `/proc/stat` — время загрузки ядра в секундах Unix. Скрипт переводит его в UTC; это не время запуска nginx или выполнения скрипта. Имя берётся из `hostname`; при необходимости строку `server_name=$(hostname)` можно заменить, например, на `server_name='office-ubuntu-01'`. `jq` корректно экранирует строки JSON. Подмена файла выполняется только после успешного формирования нового ответа.

## 3. Настроить автоматическое обновление

Создать службу:

```bash
sudo nano /etc/systemd/system/uptime-harbor-health.service
```

Содержимое:

```ini
[Unit]
Description=Generate UptimeHarbor server health JSON
After=time-sync.target

[Service]
Type=oneshot
RuntimeDirectory=uptime-harbor-health
RuntimeDirectoryMode=0755
RuntimeDirectoryPreserve=yes
ExecStart=/usr/local/sbin/uptime-harbor-health.sh
```

Служба сама создаёт `/run/uptime-harbor-health`; вручную создавать эту папку не нужно. После перезагрузки `/run` очищается, поэтому старый JSON от предыдущей загрузки не будет выдан как актуальный.

Создать таймер:

```bash
sudo nano /etc/systemd/system/uptime-harbor-health.timer
```

Содержимое:

```ini
[Unit]
Description=Refresh UptimeHarbor server health JSON

[Timer]
OnBootSec=5s
OnUnitActiveSec=1min
AccuracySec=1s
Unit=uptime-harbor-health.service

[Install]
WantedBy=timers.target
```

Применить и сформировать первый ответ:

```bash
sudo systemctl daemon-reload
sudo systemctl start uptime-harbor-health.service
sudo systemctl enable --now uptime-harbor-health.timer
cat /run/uptime-harbor-health/health.json
```

Если запуск службы завершился ошибкой, сначала исправить её:

```bash
sudo journalctl -u uptime-harbor-health.service -n 30 --no-pager
```

Таймер обновляет файл после загрузки и каждую минуту, в том числе после коррекции системных часов. При обычном обновлении `startedAtUtc` остаётся прежним; после перезагрузки меняется. Постоянно работающего процесса скрипта нет, cron не нужен.

## 4. Настроить nginx

Открыть файл:

```bash
sudo nano /etc/nginx/sites-available/uptime-harbor-health
```

Содержимое:

```nginx
server {
    listen 8088;
    server_name _;

    access_log /var/log/nginx/uptime-harbor-health.access.log;
    error_log /var/log/nginx/uptime-harbor-health.error.log;

    location = /health {
        alias /run/uptime-harbor-health/health.json;
        default_type application/json;
        add_header Cache-Control "no-store";
    }

    location / {
        return 404;
    }
}
```

Здесь нет `allow`/`deny`: читать ответ может любой клиент, имеющий сетевой доступ к порту.

При первой настройке создать ссылку; если она уже есть после прежней настройки, повторять команду не требуется:

```bash
sudo ln -s /etc/nginx/sites-available/uptime-harbor-health /etc/nginx/sites-enabled/uptime-harbor-health
```

Проверить конфигурацию:

```bash
sudo nginx -t
```

Только при успешной проверке применить:

```bash
sudo systemctl enable --now nginx
sudo systemctl reload nginx
```

## 5. Проверить и добавить в UptimeHarbor

На Ubuntu:

```bash
curl -i http://127.0.0.1:8088/health
```

Должны вернуться HTTP `200` и JSON с `name` и `startedAtUtc`. Пример:

```json
{"name":"ubuntu-server-01","startedAtUtc":"2026-11-01T03:20:00Z"}
```

Затем выполнить проверку с машины UptimeHarbor, заменив условный `203.0.113.10` на доступный с неё IP Ubuntu:

```bash
curl -i http://203.0.113.10:8088/health
```

В Windows использовать `curl.exe` вместо `curl`. В проекте при добавлении сервера указать **Адрес проверки**:

```text
http://203.0.113.10:8088/health
```

Путь — ровно `/health`, без завершающего `/`. `127.0.0.1` подходит в проекте только тогда, когда сам UptimeHarbor запущен на этой же машине. Для сервера за роутером нужен доступный маршрут: например, проброс порта или VPN.

После первой успешной проверки проект показывает время загрузки в UTC и продолжительность работы. Новый ответ только с `name` уже не удовлетворяет контракту. Если выбран HTTPS, нужен доверенный сертификат и прямой конечный URL: redirect проект не выполняет.

| Проблема | Где проверить |
| --- | --- |
| HTTP 404 на точном `/health` | Запуск службы и наличие `/run/uptime-harbor-health/health.json` |
| HTTP 403 | Права папки `0755` и файла `0644` |
| Соединение отклонено | Состояние nginx, свободен ли порт, подключена ли конфигурация |
| Timeout с машины UptimeHarbor | Адрес, маршрутизация, настройки хостинга и роутера |
| Ошибка времени запуска в проекте | Наличие поля, UTC и правильность часов на обеих машинах |

Эта проверка подтверждает доступность HTTP endpoint и сообщает последнее время загрузки. Она не проверяет состояние всех служб, дисков и памяти; сохранённое время запуска само по себе не доказывает непрерывную доступность.
