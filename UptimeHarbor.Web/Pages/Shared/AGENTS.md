# Общий Razor layout

`_Layout.cshtml` задаёт навигацию и подключает локальные Bootstrap CSS и JS bundle из `wwwroot/lib/bootstrap`. jQuery и плагины jQuery Validation не используются и не поставляются; partial для их подключения удалён.

Сохранять логин перед POST-выходом, server-side видимость административных ссылок и совместимость с CSP. Общие assets не должны дублировать инициализацию модулей страниц.

Тёмная Bootstrap-тема сохраняется. Навигация, рабочая область и footer используют `container-fluid` с общими горизонтальными отступами, чтобы широкие таблицы использовали доступную ширину окна.

Layout однократно подключает `dist/shared/switchAnimations.js` для переключателей с `data-animated-switch`; модуль использует Web Animations API и учитывает reduced motion.
