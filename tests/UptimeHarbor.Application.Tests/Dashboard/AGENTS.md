# Тесты dashboard query

Проверяются видимость, поиск, раздельные имена сервера, UTC и uptime на materialized fake snapshot.

Использовать фиксированный `TimeProvider`. Скрытые объекты и endpoint URL не должны попадать в DTO; fake query не доказывает SQL-проекцию.
