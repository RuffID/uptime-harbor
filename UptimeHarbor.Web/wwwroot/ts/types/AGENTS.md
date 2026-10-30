# Локальные SignalR declarations

Cluster lifecycle использует start/stop и automatic reconnect. При отзыве доступа и pagehide закрывать connection, отменять read requests и очищать защищённый редактор.

`signalr.d.ts` описывает используемую приложением часть глобального `signalR` browser API.

Это локальный контракт TypeScript, а runtime asset находится в `wwwroot/vendor/signalr`. Менять declarations вместе с реально используемыми вызовами и проверять совместимость asset; не копировать сюда реализацию сторонней библиотеки.
