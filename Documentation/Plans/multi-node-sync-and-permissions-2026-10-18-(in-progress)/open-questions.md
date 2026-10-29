# Открытые вопросы

## Вопросы клиенту

Открытых вопросов нет.

## Технические вопросы разработчику

Открытых вопросов нет.

Согласованные правила находятся в [требованиях](business-requirements.md), условия связи и рабочий масштаб — в [техническом проектировании](technical-design.md#11-среда-масштаб-и-порядок-проверки), порядок минимальных тестов и самостоятельной проверки — в [этапе 10](10-verification-and-acceptance/main.md). Конкретные API и алгоритмы зафиксированы в завершённом [этапе 01](01-protocol-and-contracts/main.md); подтверждение по официальным исходникам и непроведённые runtime-проверки указаны отдельно.

Этапы 01–06 — `completed`; общий план — `in-progress`. Этапы 07–10 не начаты, 09.2/09.3 — `cancelled`. Реализованы общий sign-in/session/account pipeline и monitoring capture/sequence, accepted availability/coverage, incidents/events/intents, snapshot/WAL replay и состав backlog summaries. Четыре production builds и необходимые изолированные тесты выполнены; [результаты](../../Technical%20documentation/Testing.md#проверка-этапа-06). Admission/trust/activation/delivery и browser/worker/sender gates остаются закрытыми. Новая migration не нужна; подготовленная схема к БД не применялась. Реальные SQL/peers/endpoints/Telegram/browser, crash/fsync, эксплуатация и часовая цель не проверялись. После 06.4 работа остановлена; этап 10 отдельно не начинался.
