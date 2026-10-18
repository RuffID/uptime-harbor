# Общий Razor layout

`_Layout.cshtml` задаёт навигацию, локальные assets и antiforgery meta token; shared partial подключает validation assets.

Сохранять логин перед POST-выходом, server-side видимость административных ссылок и совместимость с CSP. Общие assets не должны дублировать инициализацию модулей страниц.
