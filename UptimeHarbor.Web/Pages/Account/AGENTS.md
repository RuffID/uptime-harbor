# Страницы учётной записи

Login/Logout PageModel вызывают `IUserSignInService`; AccessDenied отображает отказ. Login принимает локальный return URL и применяет login rate-limit policy.

Ширину формы входа ограничивать Bootstrap container/grid: на телефоне доступная ширина, начиная с sm — более узкие колонки; не растягивать поля на всю ширину общего fluid layout.

Не различать неизвестный логин, неверный пароль, disabled и lockout в видимой ошибке. Выход выполняется POST с antiforgery; не переносить Identity store и парольное логирование в PageModel.
