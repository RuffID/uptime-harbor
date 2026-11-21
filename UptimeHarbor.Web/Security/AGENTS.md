# Web security adapters

ClusterLegacyWriterFilter проверяет accepted Administrator до legacy action. В enabled legacy dashboard/object/chat read/write не подменяют новый accepted API; разрешены общие account routes и существующий accepted test-send. Disabled сохраняет локальные действия. Фильтр не открывает browser readiness gate.

`CurrentUserAccessContext` преобразует claims в прикладной access context. `WebSecurityPolicies` и middleware задают login rate-limit и security headers/CSP.

Не ослаблять серверную authorization из-за скрытых элементов UI. Не добавлять широкие CSP origins ради обхода ошибки; browser/runtime-поведение проверяется отдельно от metadata и source tests.
