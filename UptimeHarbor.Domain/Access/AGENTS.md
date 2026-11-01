# Доменная видимость

`ServerVisibilityPermission` и `ContainerVisibilityPermission` сохранены только для совместимости прежних Application-контрактов. В целевую EF-модель они не входят; новые ACL, владельцев и домашний узел пользователя не добавлять. Общий активный пользователь имеет единственную роль Administrator и полный доступ; независимая модель аккаунта находится в `Cluster`.

Не добавлять Identity-типы или запросы к хранилищу. Изменения правил связей отражать в `Documentation/Technical documentation/Authentication and visibility.md`.
