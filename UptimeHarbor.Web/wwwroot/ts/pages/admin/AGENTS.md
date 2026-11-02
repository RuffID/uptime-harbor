# Административные TypeScript-модули

Node Pending — эксплуатационная заявка, не ресурсный редактор: показывать только безопасные поля/результат. Cancel использует lifecycle API и сохраняет весь запрос вместе с OperationId при retry; held renewal — явный typed Replace с текущей OperationsRevision. Diagnostic term/membership/ages/terminal errors/maintenance не являются доказательством readiness или countdown Telegram.

users.ts и notifications.ts обслуживают свои страницы через MVC API, modal и DOM. Управление объектами перенесено в pages/dashboard.ts и monitoringEditor.ts; objects.ts / containers.ts удалены.

В enabled-режиме эти страницы и Index подключают только cluster.ts. clusterApi использует shared HTTP; clusterDraft/clusterRefresh удерживают отправленный request и отбрасывают stale responses; clusterViews рисует безопасные DTO, clusterEditor управляет typed modal без raw JSON. Pending и accepted не смешивать. После reconnect перечитывать защищённые accepted/Pending/diagnostics; 401/403 очищает редактор и закрывает SignalR. Ввод, включая новый пароль, хранится только в памяти. Новая команда/rebase — явное действие; повтор исходной отправки сохраняет ID и payload.

Использовать общий HTTP-модуль и DOM API без строкового `innerHTML`. Не перезагружать страницу ради изменений; сохранять antiforgery, отображение field errors и защиту от повторной инициализации.

Коллекции отображаются через `shared/dataTable.ts`. Фильтры располагаются слева над таблицей, создание — справа; действия строк используют компактные текстовые кнопки. Permissions/owner UI отсутствует. Динамические cluster-действия делегируются стабильному корневому контейнеру; refresh строк не пересоздаёт редактор.

`users.ts` обслуживает Disabled: открывает состояние аккаунта в Bootstrap modal и возвращает фокус в список после закрытия. Пока идёт сохранение, закрытие блокируется; при ошибке окно остаётся открытым, при успехе закрывается после обновления списка. Список серверов/контейнеров ради permissions не загружать.

При сохранении пользователя запрос `/enabled` отправляется только при отличии переключателя от `isEnabled` выбранной записи; self/last-admin ограничения сохраняются.

Кластерная панель Monitoring объединяет Server / Container (подпись «Приложение»). Тип выбирается только при создании; родитель доступен только приложению. Сохранять прежние wire-коды, Pending/conflict lifecycle и версии. Показывать одно окно; ошибки оставлять внутри, после закрытия возвращать фокус в список.

Для чата без привязок в таблице Telegram оставлять ячейки типа/объекта/действий привязки пустыми, без прочерков.
