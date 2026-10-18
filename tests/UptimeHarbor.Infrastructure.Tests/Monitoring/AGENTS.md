# Тесты monitoring adapters

HTTP checker использует fake `HttpMessageHandler`; cycle/worker — fake scopes, управляемые completion sources и время.

Не подменять fake handler реальной сетью. Запуск отдельного worker на заглушках не должен запускать Web-host; сохранять проверки timeout, cancellation и ограниченной параллельности.
