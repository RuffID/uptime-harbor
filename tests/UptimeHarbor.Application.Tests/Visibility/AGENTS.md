# Тесты получателей событий

`MonitoringEventRecipientResolverTests` проверяет общий visibility rule и batch read-контракт на подставных данных.

Проверять полный доступ Administrator и отсутствие доступа через старые server/container permission-наборы. Отдельно сохранять ограничение количества read-вызовов; фактический SQL проверяется инфраструктурным набором.
