# Peer perimeter and Raft hosting

- AddClusterWeb собирает monitoring worker/pump/coordinator с revision transport/dispatcher; Infrastructure регистрирует scoped capture рядом с его UoW. Проверять и самостоятельные DI modules, и итоговый граф без запуска host.
- Monitoring payload storage generation должен совпадать с authenticated peer envelope до dispatch всего batch. Health отдаёт только safe MonitoringProgress; UI/SignalR payload не расширять внутренним image.
- ClusterMonitoringWorker и ClusterDeliveryWorker ничего не делают в Disabled; enabled проверки не обходят readiness/recovery/activation. Delivery worker восстанавливает очередь перед renewal и обновляет authority независимо от in-flight transport.
- ClusterRaftHandle отделяет ссылку writer на текущий native Raft от runtime, который закрывает sender gate. Сохранять отсутствие DI цикла runtime → gate → journal → runtime; handle не является доказательством readiness/authority.
- Delivery payload storage generation проверять против authenticated envelope до dispatch; internal results не получают фиктивного Administrator.

- Peer listener имеет отдельный порт и canonical host; middleware стоит до forwarded headers, cookie authentication и DotNext. Пользовательские endpoints на этом listener запрещены.
- Nginx certificate headers принимать только на изолированном listener от точного allowlist IP; проверять client CA, SAN и accepted fingerprint на каждом запросе. Требуются CRL, запрет прямого ingress и выключенный early data.
- Регистрация DotNext не разрешает ранний hosted start или resolve WAL. Управляемый runtime восстанавливает snapshot до создания persistent state. Дефект БД закрывает RPC перед stop, не меняя membership.
- Дочерний DI DotNext должен содержать ASP.NET diagnostic/metrics зависимости native handler. Проверять фактическое создание cluster/store/pipeline после checkpoint без Start и сети: один ValidateOnBuild не раскрывает зависимости middleware/factory.
- Health не зависит от SQL в обработчике, возвращает только safe statuses/ages и challenge. Управление составом требует принятой авторизации общего Administrator; локальная cookie сама по себе недостаточна.
- Аккаунтный bootstrap выполняется после восстановления/replay только точной командой из trusted admission. Его узкое исключение native writer не открывает общий CanWork/browser/worker/sender; Join/Restart не создают аккаунт.
- Командный batch принимает только проверенный peer origin и валидируется целиком до dispatch. Replication/snapshot workers не подтверждают readiness самостоятельно; replay должен доказать действительный принятый префикс. Backlog не содержит payload.
