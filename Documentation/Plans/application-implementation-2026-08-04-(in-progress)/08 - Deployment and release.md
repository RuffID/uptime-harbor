# Этап 8. Развёртывание и выпуск

## Статус

**Приостановлен.** Обязательный внешний `Config/config.json`, безопасный example, Serilog human-readable console/rolling File sinks, persistent Docker log volume, internal HTTP behind Nginx и automatic database startup с backup/migration реализованы в коде. Deployment-шаблоны, Nginx, Docker Compose и orchestration не входят в требования первой версии. Web-host, runtime-загрузка config, log volume permissions, реальный SQL Server backup/migration и deployment smoke намеренно не выполнялись.

## Цель

Зафиксировать границы развёртывания первой версии без добавления Docker Compose или другого orchestration-артефакта.

## Работы

1. Подготовить production Dockerfile для исполняемого проекта `UptimeHarbor.Web`, собирающий также его зависимости Domain, Application и Infrastructure:

   - multi-stage build;
   - запуск не от root, если это не мешает целевому окружению;
   - корректный порт приложения;
   - health check или совместимый `/health` endpoint.

2. Не добавлять Docker Compose и не включать orchestration в критерии первой версии.
3. Поддерживать полный `Config/config.example.json` без секретов; обязательный runtime-файл `Config/config.json` предоставляется снаружи и не входит в git, build output или publish.
   - сохранять человекочитаемые логи в console и rolling-файлах во внешнем `/app/Logs` volume с ограниченными правами, size limit и retention;
4. Не добавлять deployment-шаблоны, Nginx-конфигурацию и orchestration в состав первой версии.
5. Описать запуск Web с автоматическим backup/application pending migrations:

   - создание production-конфигурации;
   - запуск SQL Server;
   - доступный SQL Server backup path и единственный migrating Web instance;
   - запуск приложения и проверка migration startup;
   - вход bootstrap-администратором;
   - немедленная смена начального пароля.

6. Добавить graceful shutdown фонового мониторинга.
7. Подготовить краткую инструкцию обновления версии приложения и схемы базы.

## Финальная проверка

Deployment smoke-проверки не выполняются в первой версии. Остаются только сборки и изолированные тесты прикладных проектов; runtime-проверки внешнего окружения относятся к отдельному будущему этапу.

## Результат этапа

Docker Compose и orchestration-артефакты не являются частью первой версии; дальнейшая автоматизация развёртывания вынесена за её границы.
