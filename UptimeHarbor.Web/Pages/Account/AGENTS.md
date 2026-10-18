# Страницы учётной записи

Login/Logout PageModel вызывают `IUserSignInService`; AccessDenied отображает отказ. Login принимает локальный return URL и применяет login rate-limit policy.

Не различать неизвестный логин, неверный пароль, disabled и lockout в видимой ошибке. Выход выполняется POST с antiforgery; не переносить Identity store и парольное логирование в PageModel.
