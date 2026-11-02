# Подготовка Ubuntu-сервера к мониторингу

Это настройка проверяемого Ubuntu-сервера без контейнеров. UptimeHarbor обращается к nginx по HTTPS через домен на порту `43871` и получает внутреннее имя машины и время загрузки ОС в UTC:

```json
{"name":"ubuntu-server-01","startedAtUtc":"2026-11-01T03:20:00Z"}
```

`Z` означает UTC, то есть часовой пояс `+00:00`. Часовой пояс самой Ubuntu менять не требуется. Её часы и часы машины UptimeHarbor должны быть синхронизированы.

Предполагается, что на Ubuntu уже работает nginx с настроенным доменом и выпущенным сертификатом Let's Encrypt. Для мониторинга в существующий конфигурационный файл сайта добавляется ещё один `server`-блок на порту `43871`, использующий тот же домен и сертификат.

Endpoint открыт для всех, без авторизации и ограничений по IP. Если UFW включён, для него добавляется разрешение TCP `43871` по пункту ниже. В примерах используется **условный домен `health.example.com`: заменить его везде на свой уже настроенный домен**. Итоговый адрес проверки — `https://health.example.com:43871/health`.

Команды ниже выполняются на проверяемой Ubuntu оператором. Настройка в реальном окружении в рамках этой задачи не выполнялась.

## 1. Проверить исходные условия и установить пакеты

Нужны:

- уже работающий HTTPS-сайт в nginx на этой Ubuntu;
- домен, на который действует его сертификат Let's Encrypt;
- пути из рабочего блока nginx: `ssl_certificate` и `ssl_certificate_key`;
- свободный TCP-порт `43871`, доступный с машины UptimeHarbor.

Если доступ проходит через роутер или ограничения хостинга, обеспечить маршрут к TCP `43871`. Домен должен позволять обращаться к этому порту на Ubuntu; при использовании HTTP-прокси/CDN проверить поддержку порта `43871`.

Установить инструменты для JSON и проверки ответа:

```bash
sudo apt update
sudo apt install jq curl
```

### Если UFW включён

Если на сервере установлен UFW, проверить его состояние:

```bash
sudo ufw status
```

При статусе `active` разрешить входящие подключения на порт мониторинга с комментарием:

```bash
sudo ufw allow 43871/tcp comment 'UptimeHarbor HTTPS monitoring'
```

Проверить правило и комментарий:

```bash
sudo ufw status numbered
```

Правило разрешает входящие TCP-подключения на `43871` с любых IP и применяется сразу, без перезапуска UFW. Если UFW не установлен или выключен (`inactive`), этот пункт пропустить.

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

## 4. Добавить блок в существующий конфиг nginx

Открыть действующий конфигурационный файл своего сайта. Например, если он находится по такому пути:

```bash
sudo nano /etc/nginx/sites-available/health.example.com
```

Путь выше — пример: использовать именно свой уже подключённый конфигурационный файл. Рядом с существующими `server`-блоками, на том же уровне, добавить следующий блок:

```nginx
server {
    listen 43871 ssl;
    server_name health.example.com;

    ssl_certificate /etc/letsencrypt/live/health.example.com/fullchain.pem;
    ssl_certificate_key /etc/letsencrypt/live/health.example.com/privkey.pem;

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

В `server_name` указать свой домен. Значения `ssl_certificate` и `ssl_certificate_key` **скопировать из уже работающего HTTPS-блока**. Папка сертификата в `/etc/letsencrypt/live/` может называться иначе, чем домен: в примере указаны условные пути. Один сертификат можно использовать для того же домена на разных портах.

Добавляемый блок должен находиться вне других `server`-блоков. Если блок мониторинга на `43871` уже добавлялся ранее, изменить его вместо создания дубликата. Читать `/health` по HTTPS может любой клиент, имеющий сетевой доступ к порту; `allow`/`deny` и авторизации нет.

Проверить конфигурацию и только при успехе перезагрузить её:

```bash
sudo nginx -t && sudo systemctl reload nginx
```

## 5. Проверить и добавить в UptimeHarbor

На Ubuntu:

```bash
curl -i --resolve health.example.com:43871:127.0.0.1 https://health.example.com:43871/health
```

`--resolve` направляет локальный запрос на nginx, сохраняя правильное имя домена для HTTPS и проверки сертификата. Должны вернуться HTTP `200` и JSON с `name` и `startedAtUtc`. Пример:

```json
{"name":"ubuntu-server-01","startedAtUtc":"2026-11-01T03:20:00Z"}
```

Затем выполнить проверку с машины UptimeHarbor, уже через обычный DNS:

```bash
curl -i https://health.example.com:43871/health
```

В Windows использовать `curl.exe` вместо `curl`. В проекте открыть **Панель мониторинга**, нажать **Создать**, выбрать тип **Сервер**, задать имя и указать **Адрес проверки**:

```text
https://health.example.com:43871/health
```

Путь — ровно `/health`, без завершающего `/`. В адресе проекта использовать домен, на который выдан сертификат, а не IP Ubuntu; порт `43871` указывается явно. Код UptimeHarbor менять не требуется: схема, домен и порт берутся из URL. При переводе уже добавленного сервера изменить его адрес проверки на новый HTTPS URL.

После первой успешной проверки проект показывает время загрузки в UTC и продолжительность работы. Ответ только с `name` не удовлетворяет контракту. Указывать сразу конечный HTTPS URL: redirect проект не выполняет. Не отключать проверку сертификата через `curl -k`: успешная проверка должна подтверждать доверенную цепочку, имя домена и срок действия сертификата.

| Проблема | Где проверить |
| --- | --- |
| HTTP 404 на точном `/health` | Запуск службы и наличие `/run/uptime-harbor-health/health.json` |
| HTTP 403 | Права папки `0755` и файла `0644` |
| Соединение отклонено | Состояние nginx, свободен ли порт, подключена ли конфигурация |
| Timeout с машины UptimeHarbor | Адрес, маршрутизация, настройки хостинга и роутера, правило для `43871/tcp` при включённом UFW |
| Ошибка сертификата | Используется ли нужный домен, полная цепочка `fullchain.pem`, срок действия и системные часы |
| Ошибка времени запуска в проекте | Наличие поля, UTC и правильность часов на обеих машинах |

Эта проверка подтверждает доступность HTTP endpoint и сообщает последнее время загрузки. Она не проверяет состояние всех служб, дисков и памяти; сохранённое время запуска само по себе не доказывает непрерывную доступность.
