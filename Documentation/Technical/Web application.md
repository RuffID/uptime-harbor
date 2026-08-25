# Web-приложение

## Composition root

`UptimeHarbor.Web/Program.cs` выполняет только настройку host, модульные DI-вызовы, middleware и endpoint mapping. Прикладная логика в нём не размещается.

Текущий pipeline:

1. production exception handler и HSTS вне Development;
2. HTTPS redirection;
3. routing;
4. authorization;
5. static assets;
6. MVC controllers;
7. Razor Pages;
8. SignalR hub;
9. health check.

Authentication middleware будет добавлен вместе с этапом аутентификации; сейчас полноценный вход ещё не реализован.

## Типы endpoint

- Пользовательские страницы реализуются Razor Pages.
- HTTP API должно реализовываться MVC-контроллерами с attribute routing.
- Minimal API handlers `MapGet`, `MapPost`, `MapPut`, `MapDelete` и `MapPatch` не используются.
- SignalR hub сопоставлен по пути `/hubs/monitoring`.
- Стандартный health-check endpoint сопоставлен по пути `/health`.

`MapHealthChecks` и `MapHub` являются специализированными mappings платформы, а не обработчиками бизнес-API.

## Presentation DI

`AddWebPresentation` регистрирует controllers, Razor Pages, SignalR и health checks. Новые presentation-зависимости должны добавляться через этот или более узкий extension-метод, а не прямыми lifetime-регистрациями в `Program.cs`.

## Razor Pages

Текущие страницы являются стартовым шаблоном. По мере реализации PageModel должны зависеть от Application use case и работать с DTO/UI-моделями, не передавая EF-сущности в Razor.

Изменяющие формы должны использовать POST, antiforgery и серверную авторизацию. API-контроллеры должны возвращать явные HTTP-контракты и также проверять роль/доступ.

## SignalR

`MonitoringHub` пока не содержит методов. В целевой реализации сервер сам определяет пользователя соединения, не передаёт события скрытых объектов и после переподключения позволяет клиенту перечитать актуальный snapshot.
