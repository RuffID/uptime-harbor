# Сборка и развёртывание

## Состояние реализации

В Web-проекте есть multi-stage Dockerfile, который восстанавливает и собирает Domain, Application, Infrastructure и Web, копирует локальные DLL из `libs` и запускает `UptimeHarbor.Web.dll` на ASP.NET runtime image.

Docker Compose, production-конфигурация, Nginx-пример, хранение Data Protection keys/volume и полная инструкция выпуска ещё не подготовлены. Этап развёртывания имеет статус «Не начат».

## Целевая схема

```text
Internet
   |
Nginx + TLS
   |
UptimeHarbor.Web container
   |
PostgreSQL container + persistent volume
```

Наружу публикуется только reverse proxy. PostgreSQL остаётся во внутренней сети. Web-контейнер должен иметь сетевой доступ к endpoint, которые он проверяет.

## Конфигурация и данные

Production JSON с секретами монтируется извне и не включается в image. PostgreSQL data, Identity Data Protection keys и другие долговечные данные должны переживать замену контейнера.

Перед запуском новой версии схема БД обновляется отдельным шагом. Автоматическое применение миграций при старте Web отключено; подробности находятся в [Database migrations.md](Database%20migrations.md).

## Reverse proxy

Nginx должен передавать forwarded scheme/host, поддерживать WebSocket upgrade для SignalR и использовать разумные proxy timeout. Cookies в production должны учитывать фактическую HTTPS-схему за proxy.

## Health и остановка

Web предоставляет `/health`. После появления фонового monitoring worker health check должен различать готовность Web и критичную недоступность обязательных зависимостей.

Фоновый worker обязан корректно обрабатывать cancellation при остановке контейнера и не начинать новые проверки после запроса shutdown.
