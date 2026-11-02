# Клиент главной панели

В enabled-режиме Index использует admin/cluster.ts: accepted API и per-observer availability/coverage/metadata вместо legacy aggregate dashboard. Disabled сохраняет dashboard.ts. Ни один UI-модуль не открывает server readiness gates.

dashboard.ts управляет поиском, фильтрами типа/статуса, общей таблицей Server / Application, uptime и SignalR. monitoringSnapshot.ts объединяет связанные и самостоятельные приложения с серверами; monitoringEditor.ts владеет modal CRUD. Родителя показывать отдельной колонкой; таблицу и редактор не пересоздавать при SignalR refresh.

Сохранять окно объединения сигналов 200 мс, отмену прежнего fetch и немедленное обновление после reconnect. Штатный `AbortError` не показывать как сбой; server-side visibility остаётся обязательной.

Статус SignalR описывать как автоматическое обновление данных: подключение, работа, восстановление или недоступность с предложением нажать «Обновить».

В строке сервера отображать UTC `startedAtUtc` и `uptimeSeconds` из snapshot теми же форматтерами, что у контейнера; неизвестное время показывать как отсутствие данных.
