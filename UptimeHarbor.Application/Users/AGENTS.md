# Управление пользователями

`UserManagementService` обслуживает Disabled через локальный account store. В сетке `SharedUserManagementService` подаёт Create/Update/Password/SetEnabled/Delete через существующий Pending/reducer; legacy writers запрещены. `AccountCommandGate` сохраняет исходные salted credentials при retry между scopes. Проверять активного принятого актора при подаче и принятии; Pending не меняет вход.

Запрещать отключение/удаление текущего и последнего включённого администратора. Ресурсных назначений и API их изменения нет; старые DTO-поля возвращают пустые наборы до переработки UI. Expected store errors преобразуются в прикладной результат.
