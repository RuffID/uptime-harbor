# UptimeHarbor.Web

## Ответственность

Исполняемый ASP.NET Core-проект содержит composition root, Razor Pages, MVC-контроллеры, SignalR, Web-конфигурацию и статические ресурсы.

## Правила

- `Program.cs` содержит только builder, вызовы модульных DI-расширений, middleware и endpoint mapping.
- HTTP API реализовывать контроллерами; не добавлять `MapGet`, `MapPost` и другие Minimal API handlers.
- Razor Pages использовать для страниц, контроллеры — для HTTP API, SignalR hub — для push-обновлений.
- Не обращаться к EF Core или Telegram API напрямую из UI.
- Изменяющие запросы защищать авторизацией и antiforgery согласно типу клиента.
- При изменении Web pipeline, endpoint или UI-границ обновлять этот файл и `Documentation/Technical/Web application.md`.
- Все Razor Pages, кроме явно разрешённых страниц входа и ошибки, требуют аутентификации; административное API требует роль `Administrator`.
- `ServiceResult` преобразовывать в единый JSON envelope через Web-мэппер, не помещая HTTP-статусы в Application.
