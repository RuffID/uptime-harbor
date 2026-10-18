# Администрирование объектов

`ObjectAdministrationService` проверяет administrator policy и координирует изменения через `IObjectAdministrationUnitOfWork`. `IObjectAdministrationChangePublisher` сообщает Web об успешном сохранении.

Сохранять optimistic concurrency через `Version`, проверку уникальности имени и подтверждение количества удаляемых контейнеров. Disable/endpoint reset отменяет recovery прежнего incident в той же транзакции; publisher вызывается после сохранения.
