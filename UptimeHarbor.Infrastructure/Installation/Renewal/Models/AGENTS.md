# Local renewal records

Только публичные immutable локальные metadata/pins; private key здесь не размещать. References связаны с installation/cluster/node/storage/previous credential. BindIntent хранит исходную unstamped команду и correlation с Authorize/DER; Binding хранит exact accepted result и Bind/overlap UTC. CredentialRevision вычисляется из result.Index, отдельной изменяемой revision нет. Не публиковать модели как wire DTO или authority; даже Binding не означает installed/Ready.

Manifest ссылается на существующий slot и закрепляет material/Bind digests. InstalledReference связывает IssueId/revision с digest manifest, Confirmation закрепляет digest ссылки. Каждая запись хранит принятый cut. Возвращать LocalVoterCredentialInstallation только после fresh проверки всей цепочки. Runtime activation оформляется отдельно.

В intent ContinueCredential входят BootId и BootChallenge исходного процесса. Штатный startup также закрепляет process epoch/recovery, membership и пару адресов лидера. Опциональные поля сохраняют формат обычной ротации и существующих локальных intent. Manifest продолжения использует digest точной регистрации в поле authorization и digest результата accepted Bind.

Replay связывает exact Bind и replayed head для карантина. Сохранять эту сверку до свежей majority reconciliation и рабочей публикации. Модель живёт в памяти процесса.

PendingBindReplay связывает исходный локальный intent, команду Bind, точный принятый Authorize и sealed replay head. Хранить корреляцию только в снимке процесса, включая промежуток между fresh majority-read и Process/Enable. Отсутствие решения Bind в срезе сохраняет незавершённый intent.
