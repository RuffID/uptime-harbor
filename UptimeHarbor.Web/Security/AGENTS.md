# Web security adapters

`CurrentUserAccessContext` преобразует claims в прикладной access context. `WebSecurityPolicies` и middleware задают login rate-limit и security headers/CSP.

Не ослаблять серверную authorization из-за скрытых элементов UI. Не добавлять широкие CSP origins ради обхода ошибки; browser/runtime-поведение проверяется отдельно от metadata и source tests.
