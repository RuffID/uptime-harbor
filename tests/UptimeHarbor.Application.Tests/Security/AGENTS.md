# Тесты administrator policy

Проверяются аутентификация, enabled и роль через fake access context, включая отказы административных сценариев до обращения к зависимостям.

Controller authorization не заменяет Application policy; сохранять различие `Unauthorized` и `Forbidden`.
