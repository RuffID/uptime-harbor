# Административные TypeScript-модули

`objects.ts`, `users.ts` и `notifications.ts` обслуживают свои Razor Pages через MVC API, modal-формы и динамический DOM.

Использовать общий HTTP-модуль и DOM API без строкового `innerHTML`. Не перезагружать страницу ради изменений; сохранять antiforgery, отображение field errors и защиту от повторной инициализации.
