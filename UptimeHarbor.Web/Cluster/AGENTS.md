# Peer perimeter and Raft hosting

- Peer listener имеет отдельный порт и canonical host; middleware стоит до forwarded headers, cookie authentication и DotNext. Пользовательские endpoints на этом listener запрещены.
- Nginx certificate headers принимать только на изолированном listener от точного allowlist IP; проверять client CA, SAN и accepted fingerprint на каждом запросе. Требуются CRL, запрет прямого ingress и выключенный early data.
- Регистрация DotNext не разрешает ранний hosted start или resolve WAL. Управляемый runtime восстанавливает snapshot до создания persistent state. Дефект БД закрывает RPC перед stop, не меняя membership.
- Дочерний DI DotNext должен содержать ASP.NET diagnostic/metrics зависимости native handler. Проверять фактическое создание cluster/store/pipeline после checkpoint без Start и сети: один ValidateOnBuild не раскрывает зависимости middleware/factory.
- Health не зависит от SQL в обработчике, возвращает только safe statuses/ages и challenge. Управление составом требует принятой авторизации общего Administrator; локальная cookie сама по себе недостаточна.
