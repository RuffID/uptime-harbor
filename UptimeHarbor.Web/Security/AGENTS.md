# Web security adapters

LocalSetup использует отдельные __Host-cookie/scheme/DP purpose и проверяет stamp/epoch/BootId/phase, absolute 30/idle 15 минут. SetupBoundary сверяет точные HTTPS Origin/Host у mutation, не допускает common API до activation. Общие CSS/HTTP assets обычного Disabled не блокировать. Каждая setup mutation дополнительно требует antiforgery; после входа клиент получает новый token для новой identity. Common requests и peer mutations удерживают generation/write lease до завершения.

ClusterLegacyWriterFilter проверяет accepted Administrator до legacy action. В enabled legacy dashboard/object/chat read/write не подменяют новый accepted API; разрешены общие account routes и существующий accepted test-send. Disabled сохраняет локальные действия. Фильтр не открывает browser readiness gate.

`CurrentUserAccessContext` преобразует claims в прикладной access context. `WebSecurityPolicies` и middleware задают login rate-limit и security headers/CSP.

Не ослаблять серверную authorization из-за скрытых элементов UI. Не добавлять широкие CSP origins ради обхода ошибки; browser/runtime-поведение проверяется отдельно от metadata и source tests.
