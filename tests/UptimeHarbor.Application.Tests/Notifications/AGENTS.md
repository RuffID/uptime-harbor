# Тесты notification use case

Administration и delivery тестируются через общие fake данные, repositories, scope и sender.

Различать частичный успех по chat/binding и incident, outage/recovery и known conflict. Отсутствие реального транспорта не доказывает exactly-once или restart safety.

Тестовая отправка проверяет безопасное сообщение классифицированной ошибки sender и отсутствие утечки произвольного текста исключения.
