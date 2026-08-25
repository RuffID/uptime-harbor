# Razor Pages

## Ответственность

Каталог содержит пользовательские Razor Pages и регистрацию Web-presentation сервисов.

## Правила

- PageModel координирует HTTP/UI-поток, но не содержит persistence- и доменную логику.
- Для HTTP API использовать контроллеры, а не handlers страниц или Minimal API.
- Не передавать EF-сущности напрямую в представление; использовать UI-модели и DTO.
- Изменяющие формы защищать antiforgery и серверной авторизацией.
- При изменении жизненного цикла страниц или регистрации presentation обновлять этот файл и `Documentation/Technical/Web application.md`.
