# Управление пользователями

`UserManagementService` обслуживает Disabled через локальный account store. В сетке `SharedUserManagementService` подаёт Create/Update/Password/SetEnabled/Delete через существующий Pending/reducer; legacy writers запрещены. `AccountCommandGate` сохраняет исходные salted credentials при retry между scopes. Проверять активного принятого актора при подаче и принятии; Pending не меняет вход.

Запрещать отключение/удаление текущего и последнего включённого администратора. Ресурсных назначений и UI/API их изменения нет; compatibility DTO-поля возвращают пустые наборы. Expected store errors преобразуются в прикладной результат.

AccountChangeRequest поддерживает ResolvesConflictId и Replace/ConfirmRestored через существующий Pending. Password для восстановленной/конфликтной заявки вводится заново; защищённый вариант не возвращается клиенту. Повтор control сохраняет prepared credentials, а Outcome показывает настоящий результат replacement/acceptance race.
