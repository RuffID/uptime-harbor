# 05а. Отправитель Telegram и сводки

Прежний этап отложен. Статус на момент пересмотра — `not-started`.

**Объём MVP.** Отправляет один разрешённый узел. Обычно это №1. При его отказе инженер передаёт отправку №2 и после восстановления вручную возвращает её №1. На №3 отправка запрещена, токена нет. При сверке проверить существующий способ передачи роли. Автоматическая сводка за простой отложена.

[Порядок работы над MVP](../main.md#дальнейшие-действия)

<details>
<summary>Архив прежнего плана · 10 заданий</summary>

- [05a.01. Явное назначение единственного sender](05a.01-sender-assignment.md)
- [05a.02. Authority follower и отделение от лидерства](05a.02-sender-authority.md)
- [05a.03. RenewSender и BeginDelivery через лидера](05a.03-renew-and-begin.md)
- [05a.04. Capture результата и прежние attempts](05a.04-result-capture-and-legacy.md)
- [05a.05. Период простоя и устойчивый cut](05a.05-outage-and-sealed-cut.md)
- [05a.06. Стабильный состав и части сводки](05a.06-stable-summary-parts.md)
- [05a.07. Атомарное принятие плана и suppression](05a.07-atomic-plan-and-suppression.md)
- [05a.08. Изменение bindings и replacement частей](05a.08-eligibility-and-replacement.md)
- [05a.09. Доставка частей и подтверждение coverage](05a.09-delivery-and-coverage-ack.md)
- [05a.10. Статусы sender и изолированная приёмка](05a.10-status-evidence-and-docs.md)

</details>
