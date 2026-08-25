# Web-приложение

## Composition root

`UptimeHarbor.Web/Program.cs` выполняет только настройку host, модульные DI-вызовы, middleware и endpoint mapping. Прикладная логика в нём не размещается.

Текущий pipeline:

1. production exception handler и HSTS вне Development;
2. HTTPS redirection;
3. routing;
4. authentication;
5. authorization;
6. static assets;
7. MVC controllers;
8. Razor Pages;
9. SignalR hub;
10. health check.

До построения pipeline вызывается инфраструктурный bootstrap ролей и начального администратора. Миграции при этом не применяются автоматически.

## Типы endpoint

- Пользовательские страницы реализуются Razor Pages.
- HTTP API должно реализовываться MVC-контроллерами с attribute routing.
- Minimal API handlers `MapGet`, `MapPost`, `MapPut`, `MapDelete` и `MapPatch` не используются.
- SignalR hub сопоставлен по пути `/hubs/monitoring`.
- Стандартный health-check endpoint сопоставлен по пути `/health`.
- API чтения доступных объектов сопоставлено под `/api/objects`.
- API управления пользователями сопоставлено под `/api/admin/users` и требует роль `Administrator`.

`MapHealthChecks` и `MapHub` являются специализированными mappings платформы, а не обработчиками бизнес-API.

## Presentation DI

`AddWebPresentation` регистрирует controllers, Razor Pages, SignalR и health checks. Новые presentation-зависимости должны добавляться через этот или более узкий extension-метод, а не прямыми lifetime-регистрациями в `Program.cs`.

## Razor Pages

Страницы входа и выхода находятся в `/Account`. Для всего каталога Razor Pages действует обязательная авторизация; анонимный доступ разрешён только странице входа и странице ошибки.

Изменяющие формы должны использовать POST, antiforgery и серверную авторизацию. API-контроллеры должны возвращать явные HTTP-контракты и также проверять роль/доступ.

## SignalR

`MonitoringHub` требует аутентификацию и пока не содержит методов. Для будущего выбора получателей подготовлены `AccessibleObjectsService.CanAccessServerAsync` и `CanAccessContainerAsync`; SignalR publisher обязан использовать тот же Application-сервис, что и HTTP-чтения.
