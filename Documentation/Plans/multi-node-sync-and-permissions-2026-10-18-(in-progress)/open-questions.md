# Открытые вопросы

## Вопросы клиенту

Открытых вопросов нет.

## Технические вопросы разработчику

Открытых вопросов нет.

Согласованные правила находятся в [требованиях](business-requirements.md), условия связи и рабочий масштаб — в [техническом проектировании](technical-design.md#11-среда-масштаб-и-порядок-проверки), порядок минимальных тестов и самостоятельной проверки — в [этапе 10](10-verification-and-acceptance/main.md). Конкретные API и алгоритмы зафиксированы в завершённом [этапе 01](01-protocol-and-contracts/main.md); подтверждение по официальным исходникам и непроведённые runtime-проверки указаны отдельно.

Этапы 01–07 — `completed`; общий план — `in-progress`. Этапы 08–10 не начаты, 09.2/09.3 — `cancelled`. Реализованы accepted account/session/monitoring pipeline и восстановленная очередь Telegram, реальные attempts/results, единый gate, follower test-send, per-chat retry/recovery и отмены. Четыре production builds и минимальные изолированные проверки выполнены; [результаты](../../Technical%20documentation/Testing.md#проверка-этапа-07). Admission/trust/activation/delivery proof и общие gates остаются закрытыми, default Disabled сохранён. Новая migration не нужна; подготовленная схема к БД не применялась. Реальные SQL/peers/endpoints/Telegram/browser, crash/fsync, эксплуатация и часовая цель не проверялись. После 07.4 работа остановлена; этап 10 отдельно не начинался.
