# Общий Razor layout

_ClusterAdministration и _ClusterScripts задают общий статический UI enabled-режима: accepted/Pending/conflicts/observers/diagnostics/delivery и Bootstrap modal редактора. PageModel не загружает коллекции; antiforgery meta выдаётся partial, данные читает один cluster TypeScript entry point. Не подключать одновременно legacy module.

`_Layout.cshtml` задаёт навигацию и подключает локальные Bootstrap CSS и JS bundle из `wwwroot/lib/bootstrap`. jQuery и плагины jQuery Validation не используются и не поставляются; partial для их подключения удалён.

В меню только «Панель мониторинга» (/), Telegram и пользователи. _MonitoringEditor содержит общую modal Server / Application; необязательный родитель показывается только для приложения.

Сохранять логин перед POST-выходом, server-side видимость административных ссылок и совместимость с CSP. Общие assets не должны дублировать инициализацию модулей страниц.

Тёмная Bootstrap-тема сохраняется. Навигация, рабочая область и footer используют `container-fluid` с общими горизонтальными отступами, чтобы широкие таблицы использовали доступную ширину окна.

`_Layout.cshtml.css` задаёт общую ширину обёрток `monitoring-status-filter` на главной, «Серверах» и «Контейнерах». Правило использует `main ::deep`, чтобы применяться к разметке страниц внутри layout без inline styles, запрещённых CSP.

Layout однократно подключает `dist/shared/switchAnimations.js` для переключателей с `data-animated-switch`; модуль использует Web Animations API и учитывает reduced motion.
