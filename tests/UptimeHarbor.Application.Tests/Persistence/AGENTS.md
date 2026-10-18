# Тесты независимых read-контрактов

`PersistenceReadContractTests` проверяет материализованные read-only коллекции в Application API.

Не разрешать `IQueryable`, EF-типы и mutable collection contract ради удобства реализации. Проверка сигнатур не заменяет provider-level проверку запросов.
