# Локальные SignalR declarations

`signalr.d.ts` описывает используемую приложением часть глобального `signalR` browser API.

Это локальный контракт TypeScript, а runtime asset находится в `wwwroot/vendor/signalr`. Менять declarations вместе с реально используемыми вызовами и проверять совместимость asset; не копировать сюда реализацию сторонней библиотеки.
