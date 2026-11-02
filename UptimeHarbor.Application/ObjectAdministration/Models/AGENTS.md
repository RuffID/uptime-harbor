# Модели администрирования объектов

`AdminServerDto`, `AdminContainerDto` и `ServerDeletionImpactDto` описывают редактируемые данные и последствия удаления.

`AdminContainerDto.ServerId` nullable. `ContainerLinkUpdate` передаёт ID, версию и выбранность связи, `ContainerServerAssignment` отличает явное снятие связи от прежнего вызова service без её изменения.
AdminContainerDto также передаёт административное Name приложения; имя endpoint остаётся отдельным LastKnownName. Container в именах контрактов означает Application единой сущности MonitoredServer.

Сохранять `Version` для конкурентных изменений и раздельные административное/внутреннее имена сервера. Не подменять DTO доменными сущностями или Web request-моделями.
