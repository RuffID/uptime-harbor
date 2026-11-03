# Общие данные сетки

EnrollmentIssuer — публичная ProjectionRecord ограниченного application CA с фиксированным purpose, owner storage, validity ≤3 лет и необратимыми retirement/revocation. Private key и local issuance journal не входят в эту модель или shared snapshot. Сам issuer не предоставляет peer authority.

InvitationStatus и OnboardingAttemptStatus задают необратимые переходы приглашения и последовательные шаги попытки. OnboardingProjection — одна запись принятого состояния с verifier, без plaintext; её terminal состояние сохраняется вместе с общей историей.

PendingOperation сохраняет версию command protocol/schema при создании; прежние вызовы используют v1. Для новых schema-2 команд metadata не должна утверждать v1, а payload protection сохраняет собственный формат v1.

Чистые значения и инварианты принятых данных; здесь нет Raft, EF, транспорта и фоновых обработчиков. UUID пользователя не зависит от узла. Позиции сравниваются только внутри одной сетки и generation; log index допускает пропуски служебных записей.

SQL-модель является проекцией log + snapshot. Компактные decisions/controls и tombstones сохраняются на срок жизни generation. Возрастная очистка подробной истории требует завершения и отсутствия всех зависимостей незавершённой работы.

Защищённые credentials и payload не выводить через ToString, пользовательские DTO или логи. Наблюдения и delivery ledger описывают хранимые факты; согласование, вычисление доступности и отправка находятся за независимыми контрактами Application.

RestoreAttempts принимает полную реальную историю с непрерывными номерами 1–5 и согласованным состоянием intent. Поздний success монотонен; счётчик нельзя восстанавливать фиктивными попытками. Существующие поля проекции сохраняются, транспорт и retry policy не переносятся в Domain.

Held Pending старого backup не становится eligible после подтверждения: новая авторизованная control-операция заменяет его в общем порядке. Локальная отмена/control intent не равны принятому terminal-результату. При uniqueness conflict версия конкурирующей сущности может быть старше base revision изменяемой сущности.
