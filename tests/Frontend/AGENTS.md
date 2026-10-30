# Изолированные frontend-тесты

Compiled lifecycle API проверяется тем же fake fetch: antiforgery, точный payload control и 202 как очередь. Служебные команды не обходят shared HTTP и не означают разрешения запускать окружение.

clusterAdministration.test.mts проверяет production TS редактор/черновик/refresh с DOM-заглушкой: потеря ответа, стабильный OperationId, повторный конфликт/rebase, held confirmation, пароль только в памяти, stale responses и reconnect reads. API-проверка использует outputs штатной Web-сборки и fake fetch, включая antiforgery/202/403; перед ней нужен разрешённый Web compile-check. Bootstrap, геометрия/mobile и браузер отдельно не проверяются.

Проверять поведение общих TypeScript-компонентов через управляемые DOM-заглушки и геометрию. Запускать через `node --experimental-strip-types --test tests/Frontend/*.test.mts`, без приложения, браузера, сети и БД. `searchableSelects.test.mts` проверяет пустые/disabled списки, сохранение checkbox/radio-состояния и снятие связи. Такие проверки не заменяют визуальную проверку в браузере.
