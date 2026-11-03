# Локальные installation-модели

Immutable records профиля/шагов и setup DTO используют публичное пространство имён Installation. SetupStatus содержит только безопасные сведения о фазе; session/stamp, account/key references и authority evidence не сериализовать в него. Парольные requests имеют редактированный ToString, не журналируются целиком.
