# Сводка выполнения этапа 04

Обновлено: 2026-11-21. Этап: `in-progress`.

## Текущее состояние

**Работа приостановлена по команде пользователя.** NUI-04-20261121-21 завершилось до паузы. 04.09.04 и родительский 04.09 остаются `in-progress`. Точка возобновления — `ClusterPeerRuntime.StartNodeCoreAsync` сразу после `ContinueManagedVoterLockedAsync`, допуск к native catch-up. [Точный стык](04.09-credential-renewal/04.09.04-continue-credential.md#стык-после-карантинной-публикации).

**Координация 04.09 возобновлена по команде пользователя 2026-11-21.** [04.09.01. Повтор Authorize](04.09-credential-renewal/04.09.01-authorize-retry.md), [04.09.02. Штатный цикл](04.09-credential-renewal/04.09.02-automatic-rotation.md) и [04.09.03](04.09-credential-renewal/04.09.03-production-admission.md#nui-04-20261121-08) завершены в изолированном объёме. Узкий допуск штатной ротации подключён для managed Bootstrap Direct. В [04.09.04 ContinueCredential](04.09-credential-renewal/04.09.04-continue-credential.md#nui-04-20261121-21) подтверждено восстановление нового boot после Bind до первой карантинной публикации. Далее [native catch-up, удалённый ProcessRenewal и Activation/Enable](04.09-credential-renewal/04.09.04-continue-credential.md#стык-после-карантинной-публикации). После завершения 04.09 координация останавливается перед 04.10.

По указанию пользователя от 2026-11-18 команды, результаты и ограничения проверок конкретных назначений сохраняются в документах подэтапов. Сводка содержит краткий итог и ссылку. `Documentation/Technical documentation/Testing.md` используется для общих процедур и повторно используемых команд проекта. Прежние записи сохраняются.

Для новых чатов исполнителей указана модель GPT‑6.2 Sol (`gpt-6.2-sol`). 04.10 ожидает отдельной команды после завершения 04.09.

04.01–04.08 завершены в своих объёмах; первый незавершённый подэтап — 04.09. Ранее оставленная реализация 04.08 исследована и проверена отдельным заданием ниже; результат 04.06 не использован как её приёмка. Координатор управляет по отчётам исполнителей, без чтения кода и независимого ревью.

Координация возобновлена 2026-11-18. После A и B1 отдельные назначения закрыли B2a (durable pre-Bind intent/key/authorization/DER), B2b1 (exact accepted Bind result/recovery) и B2b2 (локальная установка и подтверждение). В ноябрьском промежуточном состоянии production rotation admission был закрыт. Приложение, БД, реальная сеть и deployment запрещены. Прежние незакоммиченные изменения сохраняются.

Уточнение пользователя 2026-11-17: после полного завершения каждого этапа локальный Git-коммит делает исполнитель. Подэтапы отдельно не коммитить; открытая обязательная приёмка не позволяет объявить этап завершённым. До полного завершения 04 Git остаётся запрещён. Push и отправка в GitHub запрещены; посторонние прежние изменения не включать автоматически.

<a id="активное-назначение"></a>

## Активное назначение

- Активных исполнителей нет. Координация приостановлена до команды пользователя.
- Последнее назначение `NUI-04-20261121-21` завершено до команды паузы. Чат `threadId: 01a1fe61-c24d-75d1-b33a-d4090663a245`, `hostId: local`, модель `gpt-6.2-sol`, итоговый cursor `e6dad2ed-adc0-4248-bbce-fa3393a6ee41:100`. Исполнитель подтвердил паузу.

Чат `NUI-04-20261121-20`: `threadId: 01a1fe55-68a4-7b71-960f-c01ad5db2889`, `hostId: local`, модель `gpt-6.2-sol`, итоговый cursor `70a94e61-2bb2-41b6-be23-72f06a6a7eb9:44`. Допуск нового boot принят по отчёту; публикация подтверждена в NUI-04-20261121-21.

Чат `NUI-04-20261121-19`: `threadId: 01a1fe3d-9683-7b02-b0b7-f3b6713b4432`, `hostId: local`, модель `gpt-6.2-sol`, итоговый cursor `1e92501f-7ab5-4d88-856c-e56468a59ce9:68`. Штатный вход до первой карантинной публикации принят по отчёту.

**Итог NUI-04-20261121-21.** Новый boot после Bind восстанавливает прежний защищённый slot и публикует прежние DER, IssueId и CredentialRevision в карантине после истечения lease, отдельного подтверждения и свежего majority. Проверены replay и закрытые gates. Четыре compile-check, MTP 257 успешно, 0 ошибок и пропусков. Миграций нет. [Матрица и команды](04.09-credential-renewal/04.09.04-continue-credential.md#nui-04-20261121-21), [следующий стык](04.09-credential-renewal/04.09.04-continue-credential.md#стык-после-карантинной-публикации).

**Итог NUI-04-20261121-20.** Проверен отдельный принятый допуск нового boot после истечения прежней lease. WAL и snapshot сохраняют исходные DER, Bind и CredentialRevision. Установка и первая публикация нового boot открыты. Шесть compile-check, MTP 269 успешно, 0 ошибок и пропусков. Миграций нет. [Матрица, команды и продолжение](04.09-credential-renewal/04.09.04-continue-credential.md#nui-04-20261121-20).

**Итог NUI-04-20261121-19.** Штатный startup сохраняет intent исходного boot до отправки, показывает принятый RequestId в журнале и доводит одобренный запрос до exact Register/DER/Bind, локальной установки и первого карантинного снимка. Недоступная история, отзыв, потеря majority и другой boot оставляют допуски закрытыми. Четыре compile-check без предупреждений и ошибок. Изолированный MTP 186 успешно, 0 ошибок и пропусков. Миграций нет. [Матрица, команды и точный стык](04.09-credential-renewal/04.09.04-continue-credential.md#nui-04-20261121-19).

Чат `NUI-04-20261121-18`: `threadId: 01a1fe28-e872-77c0-95a0-2906070c4866`, `hostId: local`, модель `gpt-6.2-sol`, итоговый cursor `21d7f3ac-d88e-4629-83c3-8f4e39585bea:66`. Проверка свежего принятого состояния до native Start принята по отчёту.

**Итог NUI-04-20261121-18.** Управляемый Bootstrap Direct получил узкий HTTPS источник принятых решений до native Start. Проверены сверка точной записи replay, fresh majority, отказы при откате, подмене, смене term/recovery, отзыве и потере majority. Четыре compile-check без предупреждений и ошибок. Изолированный MTP 176 успешно, 0 ошибок и пропусков. Миграций нет. Далее вызвать `VerifyManagedContinuationReplayAsync` в ветке expired managed Bootstrap Direct и связать срез с locked pins и установкой. [Доказательства и точное продолжение](04.09-credential-renewal/04.09.04-continue-credential.md#nui-04-20261121-18).

Чат `NUI-04-20261121-17`: `threadId: 01a1fe1a-d40c-7922-97c1-e1014e50621d`, `hostId: local`, модель `gpt-6.2-sol`, итоговый cursor `444cb100-f8be-4182-a6bf-9fde01ca37a9:47`. HTTPS-клиент принят по отчёту.

**Итог NUI-04-20261121-17.** Пользователь выбрал заранее настроенный доверенный HTTPS origin лидера. Клиент зарегистрирован в setup DI и проверен через штатный machine HTTPS ingress с потерей ответов, отсутствием majority и границей lease. Одна выдача и один Bind. Шесть compile-check без ошибок и предупреждений. Изолированный MTP 145 успешно, 0 ошибок и пропусков. Миграций нет. Продолжение перед `ManagedRestartPreparation.PreparePeerAfterReplay`, начиная с проверки свежего принятого среза до native Start и его связи с локальным durable intent. [Матрица, команды и открытые критерии](04.09-credential-renewal/04.09.04-continue-credential.md#nui-04-20261121-17).

Чат `NUI-04-20261121-16`: `threadId: 01a1fdfd-98fb-7c42-8ce6-a630b66ab0ea`, `hostId: local`, модель `gpt-6.2-sol`, итоговый cursor `71924dde-d10b-4be3-8356-1bc0b6555a5a:90`. Публикация и передача в Process приняты по отчёту.

**Итог NUI-04-20261121-16.** Защищённый slot публикуется атомарно с карантином. После accepted ProcessRenewal на той же действующей lease свежий cut допускает рабочий снимок и штатный Enable. Повторы сохраняют исходные IssueId, Bind и CredentialRevision. Шесть compile-check, MTP Application 80, Infrastructure 146 и Web 80 без ошибок и пропусков. Миграций нет. Открыт production запуск и допуск нового boot после прерывания. [Матрица, команды и точное продолжение](04.09-credential-renewal/04.09.04-continue-credential.md#nui-04-20261121-16).

Чат `NUI-04-20261121-15`: `threadId: 01a1fdef-5721-7e62-98d9-97414415859d`, `hostId: local`, модель `gpt-6.2-sol`, итоговый cursor `497bae93-b3a2-4b66-a063-dec9906b3268:35`. Локальная установка принята по отчёту.

**Итог NUI-04-20261121-15.** Установка связывает прежние key/CSR/DER с исходными OperationId/IssueId, текущей регистрацией и точным accepted Bind. При потере ответа или прерывании durable записей восстановление возвращает ту же CredentialRevision. Два compile-check и 158 изолированных MTP прошли без ошибок и пропусков. Миграций нет. Следующий шаг — публикация, граница process lifecycle и точка запуска восстановления. UI-приёмка ожидает пользователя в 07. [Матрица, команды и остаток](04.09-credential-renewal/04.09.04-continue-credential.md#nui-04-20261121-15).

Чат `NUI-04-20261121-14`: `threadId: 01a1fddb-8b60-7ee1-89b8-f1418c1fe1c8`, `hostId: local`, модель `gpt-6.2-sol`, итоговый cursor `a6bab2d6-4cd6-4a49-8f14-35ffb7a8f617:52`. Bind принят по отчёту в изолированной границе.

**Итог NUI-04-20261121-14.** Bind и точный повтор CredentialRevision проверены изолированно для managed expired leaf и лидера с локальным accepted issuer. После потери ответа и WAL replay сохраняются одна запись Bind и прежняя CredentialRevision. Шесть compile-check, MTP Application 80, Infrastructure 130 и Web 54 без ошибок и пропусков. Миграций нет. 04.09.04 остаётся в работе. Следующий шаг — локальная установка и её восстановление от принятого Bind. UI-приёмка ожидает пользователя в 07. [Матрица, команды и остаток](04.09-credential-renewal/04.09.04-continue-credential.md#nui-04-20261121-14).

Чат `NUI-04-20261121-13`: `threadId: 01a1fdc6-528e-77c0-ad77-f9abee42424b`, `hostId: local`, модель `gpt-6.2-sol`, итоговый cursor `f4b884c7-0edd-40a7-99d9-745cd7fd5b88:60`. Выдача принята по отчёту в проверенной границе managed leaf и локального accepted issuer лидера.

**Итог NUI-04-20261121-13.** Действующий лидер с локальным accepted issuer возвращает DER по точной регистрации, свежему PoP и отдельному issue nonce. После потери ответа и WAL replay получены те же DER с одной записью журнала. Четыре compile-check прошли, MTP Infrastructure 95 и Web 53 без ошибок и пропусков. Миграций нет. 04.09.04 остаётся в работе, далее exact accepted Bind. UI-приёмка ожидает пользователя в 07. [Матрица, команды и остаток](04.09-credential-renewal/04.09.04-continue-credential.md#nui-04-20261121-13).

Чат `NUI-04-20261121-12`: `threadId: 01a1fdb4-dd38-76a0-867d-7e443140886d`, `hostId: local`, модель `gpt-6.2-sol`, итоговый cursor `0be26037-1f5f-4cbc-88cb-04049827fad9:52`. Административное подтверждение принято по отчёту.

Чат `NUI-04-20261121-11`: `threadId: 01a1fd9e-f2bf-7980-8f07-b566888a1580`, `hostId: local`, модель `gpt-6.2-sol`, итоговый cursor `64cebc82-3043-4453-86b6-8b4a26a3a1ca:57`. HTTPS-часть принята по отчёту.

**Итог NUI-04-20261121-12.** На «Панели мониторинга» действующего лидера общий Administrator открывает точный запрос другого voter и подтверждает его своим паролем. Повтор возвращает исходное принятое подтверждение. Четыре .NET compile-check и TypeScript прошли, Web 51 и Infrastructure 54 без ошибок и пропусков. Миграций нет. 04.09.04 остаётся в работе. Далее accepted-выдача с durable DER и replay. UI-приёмка ожидает пользователя в 07. [Матрица, команды и критерии](04.09-credential-renewal/04.09.04-continue-credential.md#nui-04-20261121-12).

**Итог NUI-04-20261121-11.** Подключены отдельный HTTPS proof/nonce, точный accepted запрос, чтение его состояния и регистрация по принятому подтверждению. Истёкший leaf по-прежнему закрыт для обычного peer ingress. Четыре отдельных compile-check и 82 изолированных MTP-теста прошли без ошибок и пропусков. Миграций нет. Следующее назначение — общий Web UI подтверждения точного запроса. 04.09.04 остаётся в работе. [Матрица, команды и остаток](04.09-credential-renewal/04.09.04-continue-credential.md#nui-04-20261121-11).

Чат `NUI-04-20261121-10`: `threadId: 01a1fd87-06a7-7da3-b8f2-232ec930803f`, `hostId: local`, модель `gpt-6.2-sol`, итоговый cursor `129a4820-4119-4118-8b9f-e293bbe965b9:64`. Accepted-часть принята по отчёту.

**Итог NUI-04-20261121-10.** Закрыт пункт 1 из «Точного продолжения». Принятый запрос, подтверждение администратора и регистрация процесса используют отдельный typed вход и общий WAL. Проверены exact replay, граница lease, отказ без актуальной authority, snapshot/recovery и закрытый общий ingress. Шесть compile-check без предупреждений и ошибок, MTP Application 80, Infrastructure 35, Web 8, без ошибок и пропусков. Миграций нет. 04.09.04 остаётся в работе. Следующее назначение начинается с HTTPS/UI. [Матрица, команды и остаток](04.09-credential-renewal/04.09.04-continue-credential.md#nui-04-20261121-10).

Чат `NUI-04-20261121-09`: `threadId: 01a1fd7d-08e0-7752-b77a-865a921e4971`, `hostId: local`, модель `gpt-6.2-sol`, итоговый cursor `ed3450c5-aca6-4ea8-8345-8801fb03d50b:29`. Проверенный фундамент принят по отчёту.

**Итог NUI-04-20261121-09.** Проверены отдельный PoP истёкшего managed leaf, неизменная identity и кандидат process lease. Два compile-check без предупреждений и ошибок, MTP Infrastructure 18 успешно, 0 ошибок и пропусков. Согласованы подтверждение точного запроса в общем интерфейсе работающего узла и принятие нового процесса большинством после истечения прежней lease. Продолжение начинается с accepted состояния и команд, затем HTTPS/UI, durable issuance/replay и аудит. UI-приёмка ожидает пользователя в 07. Миграций нет. [Матрица, команды и остаток](04.09-credential-renewal/04.09.04-continue-credential.md#nui-04-20261121-09).

Чат `NUI-04-20261121-08`: `threadId: 01a1fd62-388f-7b63-b07a-29542c2c8c39`, `hostId: local`, модель `gpt-6.2-sol`, итоговый cursor `8c2d892c-14dd-410d-b136-3a2ef48dae72:85`. 04.09.03 принята по итоговому изолированному отчёту.

**Итог NUI-04-20261121-08.** Supervisor запускает штатную ротацию только для рабочего managed Bootstrap Direct. Подтверждены singleton и multi-voter, установка/публикация, Process/Enable, handoff/Retire и recovery после потери ответа. Шесть compile-check без предупреждений и ошибок. MTP Application 80, Infrastructure 381, Web 258, без ошибок и пропусков. Первый singleton-прогон исключён из доказательств из-за попытки SQL-соединения без установленного сеанса. Итоговый прогон выполнен с SQL-провайдером, удалённым из фикстуры, и fail-fast перехватом открытия. Миграций и развёртывания нет. [Матрица, команды, инцидент изоляции и ограничения](04.09-credential-renewal/04.09.03-production-admission.md#nui-04-20261121-08).

Чат `NUI-04-20261121-07`: `threadId: 01a1fd49-ad2b-78e2-a63d-ad24bf9e3c9b`, `hostId: local`, модель `gpt-6.2-sol`, итоговый cursor `2acb1a27-738d-4b3e-8c91-5995a0d66684:62`. Typed Retire принят по отчёту.

**Итог NUI-04-20261121-07.** Typed handoff и точный Retire подтверждены для локального и удалённого лидера, singleton и multi-voter, включая подтверждение от follower. Повтор после потери ответа и смены лидера сохраняет принятое решение. Шесть compile-check без предупреждений и ошибок. MTP Application 80, Infrastructure 362, Web 229, без ошибок и пропусков. Миграций нет. Production admission и activation закрыты. [Матрица, команды и точное продолжение](04.09-credential-renewal/04.09.03-production-admission.md#nui-04-20261121-07).

Чат `NUI-04-20261121-06`: `threadId: 01a1fd35-3e3a-7463-bd29-12499f91a64e`, `hostId: local`, модель `gpt-6.2-sol`, итоговый cursor `ee633cc7-16ba-4866-b5b7-aea7c1f757c8:57`. Typed Authorize и Bind приняты по отчёту.

**Итог NUI-04-20261121-06.** Типизированные Authorize и Bind подтверждены для локального и удалённого лидера managed Bootstrap Direct. Проверены exact replay с прежними OperationId, IssueId и DER и устойчивый точный Rejected после смены лидера. Шесть compile-check прошли без предупреждений и ошибок. MTP Application 80, Infrastructure 362 и Web 207, без ошибок и пропусков. Миграций нет. 04.09.03 открыта до handoff для Retire, рабочей регистрации worker и проверки полного цикла. [Матрица, команды и остаток](04.09-credential-renewal/04.09.03-production-admission.md#nui-04-20261121-06).

Чат `NUI-04-20261121-05`: `threadId: 01a1fd26-934c-7651-b3a3-7d4eb7bea77a`, `hostId: local`, модель `gpt-6.2-sol`, итоговый cursor `84be6042-6749-4493-bb37-fe2b14bae436:45`. Независимая часть принята по отчёту.

**Итог NUI-04-20261121-05.** Проверены точный локальный pre-Bind и отказ общего ingress, включая повтор принятой команды. Шесть проектов прошли compile-check. MTP Application 80, Infrastructure 362 и Web 64, без ошибок и пропусков. Миграций нет. 04.09.03 остаётся открытой. Продолжение начинается с typed приёма команд и доказательств лидером, затем подключение worker и сквозная проверка. [Матрица, команды и остаток](04.09-credential-renewal/04.09.03-production-admission.md#nui-04-20261121-05).

## Результат назначения 04.09.02

- Чат `NUI-04-20261121-04`: `threadId: 01a1fd08-67c1-7422-ba86-e2b5ce0702c8`, `hostId: local`, модель `gpt-6.2-sol`, итоговый cursor `3bfeaa34-ea63-422e-88f7-24a9731e7e49:69`. 04.09.02 принята в изолированном объёме по отчёту.

- `NUI-04-20261121-04` — по принятому правилу проверены handoff с другим рабочим voter и самопроверка singleton, штатный retirement после overlap, точный повтор, следующее продление и безопасный аудит. Шесть compile-check без предупреждений и ошибок. MTP Application 77, Infrastructure 420, Web 219 успешно, 0 ошибок и пропусков. Миграций нет. 04.09.02 закрыта в изолированном объёме. [Матрица и команды](04.09-credential-renewal/04.09.02-automatic-rotation.md#nui-04-20261121-04).

- Чат `NUI-04-20261121-03`: `threadId: 01a1fcf4-a748-7061-9273-fd4ee14b7235`, `hostId: local`, модель `gpt-6.2-sol`, итоговый cursor `683a735b-25d8-4d03-a94a-61b4f9870bb9:60`. Восстановление принято по отчёту, 6 compile-check и 695 тестов прошли без ошибок и пропусков.

- `NUI-04-20261121-03` — ManagedRestart с Bind intent впереди WAL проверен через native-only старт и fresh majority. Принятый Bind восстанавливается без дубля, непринятый повторяется с исходными pins после штатного входа. Следом взаимная проверка и автоматический retirement. [Матрица, команды и результаты](04.09-credential-renewal/04.09.02-automatic-rotation.md#nui-04-20261121-03).

- Чат назначения `NUI-04-20261121-02`: `threadId: 01a1fce0-28a0-7c52-b162-a8b6eb6fa8bd`, `hostId: local`, модель `gpt-6.2-sol`, итоговый cursor `9f2b5b15-8a05-44ce-8e8f-f02bd07279a7:63`. Проверенная часть принята по отчёту.

- `NUI-04-20261121-02` — проверены worker до точной публикации, возврат quorum/issuer, отмена после принятого Bind и ManagedRestart до записи Bind intent и после Bind. Шесть compile-check без предупреждений и ошибок. MTP Application 76, Infrastructure 406, Web 198 успешно, 0 ошибок и пропусков. Миграций нет. Открыты безопасное продолжение с bind-intent впереди replay, взаимная проверка и автоматический retirement. [Матрица, команды и продолжение](04.09-credential-renewal/04.09.02-automatic-rotation.md#nui-04-20261121-02).

## Результат назначения 04.09.01

- `NUI-04-20261121-01` — принятый Authorize сохраняет исходный результат. Истёкший запрос заменяется с прежним ключом после подтверждённого окончательного отказа. Четыре compile-check без предупреждений и ошибок, MTP Infrastructure 342 и Web 40 успешно, 0 ошибок и пропусков. Миграций нет. [Механизм, матрица и ограничения](04.09-credential-renewal/04.09.01-authorize-retry.md#фактическая-проверка-2026-11-21).
- Чат `threadId: 01a1fcce-c831-7ed2-b234-8399598cc277`, `hostId: local`, модель `gpt-6.2-sol`.
- Итоговый cursor `e2e7c5da-7556-4a24-a09e-71478a82eca1:54`. Зависимость принята по отчёту исполнителя.

## История паузы 2026-11-18

Задание `NUI-04-20261118-DOC-01` выполнено в документационном объёме. [Остаток B3c2/C](04.09-leaf-renewal-and-overlap.md#ограниченная-декомпозиция) разложен на 04.09.01–04.09.05. Реализация остаётся на паузе. Чат `threadId: 01a1ee41-be4e-7ac0-ae74-c7e4ac36009c`, `hostId: local`, модель `gpt-6.2-sol`. Итоговый cursor `b09b8c71-9eed-440d-b59c-9963d115b9d0:14`.

- Обозначение: `NUI-04-20261118-19`.
- Объём: 04.09-B3c2. Автоматический запуск renewal, взаимная проверка, retirement и узкий production admission после проверки полного обычного пути.
- Состояние: приостановлено внутри B3c2. Изолированно проверен проход расписания. По отчёту до паузы четыре compile-check прошли без предупреждений и ошибок. Infrastructure 388 и Web 197 успешно без ошибок и пропусков. Рабочий worker, взаимная проверка/retirement, автоматическое переоформление и production admission остаются открытыми. Возобновление только по новой команде пользователя. [Состояние, согласованное правило и точное продолжение](04.09-credential-renewal/04.09.02-automatic-rotation.md#voter-credential-scheduled-pass-0409b3c2).
- Чат: `threadId: 01a1edeb-c857-7bd3-8d22-37967653dc78`, `hostId: local`.
- Итоговый cursor остановленного хода: `10a7ac38-fc39-4063-8771-a8da917cabeb:19`.
- Модель нового чата: `gpt-6.2-sol`.

## Результат назначения ManagedRestart

- Обозначение: `NUI-04-20261118-18`.
- Итоговый cursor: `6fb5f136-d8c9-4bbe-8fbd-20721a8ca46b:29`.
- Объём: остаток 04.09-B3c1. ManagedRestart после принятого Bind при прерванной локальной установке.
- Состояние: выполнено в изолированном объёме. Два последовательных reopen подтверждают прежний Bind после majority. [Проверки и ограничения](04.09-credential-renewal/04.09-b3c1-renewal-and-restart.md#voter-credential-managed-restart-0409b3c1).
- Чат: `threadId: 01a1edd1-fd3a-7d00-bb37-0ac35ac43e0b`, `hostId: local`.
- Модель нового чата: `gpt-6.2-sol`.

## Результат назначения B3c1

- Обозначение: `NUI-04-20261118-17`.
- Итоговый cursor: `d618270e-4115-4b9e-bf4b-b6c1db0a7d95:45`.
- Объём: 04.09-B3c1. Связать подтверждённые шаги renewal в единый возобновляемый сценарий и использовать опубликованный слот для PoP следующей ротации.
- Состояние: изолированный объём проверен. Остаток B3c1 указан в 04.09.
- Чат: `threadId: 01a1edbb-186d-7392-8ec4-4e17203a8c88`, `hostId: local`.
- Модель нового чата: `gpt-6.2-sol`.

## Выполненное назначение B3b2 — ManagedRestart

- Обозначение: `NUI-04-20261118-16`.
- Итоговый cursor: `669a1526-5e74-49f1-9161-e07846a36a3c:74`.
- Объём: 04.09-B3b2. Восстановление точной CredentialRevision в существующем ManagedRestart и штатный peer RPC после переключения.
- Состояние: выполнено в изолированном объёме. [Проверки и ограничения](04.09-credential-renewal/04.09-b3b2-managed-direct.md#voter-credential-managed-restart-0409b3b2).
- Чат: `threadId: 01a1ed98-94ad-7ba2-9c6b-f129f1935af9`, `hostId: local`.
- Модель нового чата: `gpt-6.2-sol`.

## Выполненное назначение B3b2 — граница Nginx

- Обозначение: `NUI-04-20261118-15`.
- Итоговый cursor: `e07c0f91-52a9-4a8f-aa14-fec0f9cf80fc:28`.
- Объём: 04.09-B3b2. Проверка границы legacy Nginx, сохранение ручного proxy-terminated пути и защиты typed renewal на Direct/TLS passthrough.
- Состояние: выполнено. Четыре compile-check и 172 изолированных теста MTP прошли. [Матрица и точная граница](04.09-credential-renewal/04.09-b3b2-legacy-direct-and-nginx.md#voter-credential-nginx-boundary-0409b3b2).
- Чат: `threadId: 01a1ed8d-3998-78c0-969c-d44731d93e09`, `hostId: local`.
- Модель нового чата: `gpt-6.2-sol`.

## Выполненное назначение B3b2 — legacy Direct trust

- Обозначение: `NUI-04-20261118-14`.
- Итоговый cursor: `4f43ab7c-ff0f-49d7-aae7-8b51a0c11ad8:49`.
- Объём: 04.09-B3b2. Доверие к прежнему leaf ручного CA в цепочке ротации legacy Direct и совместимость после Bind с dedicated trust.
- Состояние: совместимость manual/dedicated trust подтверждена изолированно. Шесть compile-check и 557 тестов MTP прошли. [Матрица, проверки и следующий объём](04.09-credential-renewal/04.09-b3b2-legacy-direct-and-nginx.md#voter-credential-legacy-direct-trust-0409b3b2).
- Чат: `threadId: 01a1ed72-6c8c-7c00-ac91-5bab78f73ce0`, `hostId: local`.
- Модель нового чата: `gpt-6.2-sol`.

## Выполненное назначение B3b2 — legacy Direct transport

- Обозначение: `NUI-04-20261118-13`.
- Итоговый cursor: `89cb6aea-c44a-4c64-aea2-cb84f1ed6bfa:48`.
- Объём: продолжение 04.09-B3b2. Привязка typed RPC к точным TLS credentials в legacy Direct с сохранением CRL.
- Состояние: транспортный объём завершён. Полный renewal ручного CA, legacy Nginx, startup recovery и реальная TLS/CRL/reuse приёмка открыты. [Матрица и точная граница](04.09-credential-renewal/04.09-b3b2-legacy-direct-and-nginx.md#voter-credential-legacy-direct-0409b3b2).
- Чат: `threadId: 01a1ed59-d6af-7b33-9bbd-bc460ef49d31`, `hostId: local`.
- Модель нового чата: `gpt-6.2-sol`.

## Выполненное назначение B3b2 — managed Direct reuse

- Обозначение: `NUI-04-20261118-12`.
- Итоговый cursor: `3f79a82f-2674-46e9-91b8-f5371a7714b0:42`.
- Объём: продолжение 04.09-B3b2. Переиспользование managed Direct соединений с привязкой к точным TLS credentials и проверкой актуального доверия.
- Состояние: изолированный managed Direct объём завершён. Следующее ограниченное назначение — legacy Direct exact TLS binding с CRL. [Проверки и открытые критерии](04.09-credential-renewal/04.09-b3b2-managed-direct.md#voter-credential-managed-pool-0409b3b2).
- Чат: `threadId: 01a1ed44-6f92-7d20-9f05-0fdb2dff75f8`, `hostId: local`.
- Модель нового чата: `gpt-6.2-sol`.

## Выполненное назначение B3b2 — явная публикация

- Обозначение: `NUI-04-20261118-11`.
- Объём: продолжение 04.09-B3b2. Публикация подтверждённой локальной CredentialRevision для входящего и исходящего TLS и pools, восстановление после сбоя.
- Состояние: явная managed Direct публикация и изолированные проверки завершены. Production admission закрыт, весь B3b2 открыт. [Проверки и ограничения](04.09-credential-renewal/04.09-b3b2-managed-direct.md#voter-credential-runtime-publication-0409b3b2).
- Чат: `threadId: 01a1ed2e-e01a-7c61-8c36-7299ba01d26f`, `hostId: local`.
- Итоговый cursor: `199e017d-605a-4fc6-9d14-c8ac1316695e:51`.
- Модель нового чата: `gpt-6.2-sol`.

## Частично выполненное назначение B3b2 — typed Direct

- Обозначение: `NUI-04-20261118-10`.
- Объём: продолжение 04.09-B3b2. Привязка typed renewal RPC к точному credential входящего и исходящего соединения, включая legacy/dedicated trust, overlap и повторное использование.
- Состояние: ограниченный managed Direct объём выполнен. Четыре compile-check, Infrastructure 80 и Web 91 успешно. Источник авторитетного pin для pool и поддержка legacy typed transport остаются открытыми. [Доказательства и продолжение](04.09-credential-renewal/04.09-b3b2-managed-direct.md#voter-credential-typed-direct-0409b3b2).
- Чат: `threadId: 01a1ed19-f04b-7e20-8f5a-c18e5b87c4da`, `hostId: local`.
- Итоговый cursor: `9e5c3448-d83f-4275-afb5-aa7789239256:51`.
- Модель нового чата: `gpt-6.2-sol`.

## Частично выполненное назначение B3b2

- Обозначение: `NUI-04-20261118-09`.
- Объём: 04.09-B3b2. Привязка RPC к сертификату соединения и смена входящего и исходящего TLS/pools с восстановлением.
- Состояние: ограниченная часть выполнена и проверена. Следующий объём — привязка typed renewal и legacy/dedicated TLS, атомарная публикация credential/pools и recovery. [Результат и точное продолжение](04.09-credential-renewal/04.09-b3b2-managed-direct.md#voter-credential-managed-response-0409b3b2).
- Чат: `threadId: 01a1ed0f-bc71-7933-9543-7648d614f737`, `hostId: local`.
- Итоговый cursor: `1f81f144-c937-40ee-aa46-5f507336c16e:28`.
- Модель нового чата: `gpt-6.2-sol`.

## Завершённое назначение B3b1

- Обозначение: `NUI-04-20261118-08`.
- Объём и состояние: **04.09-B3b1 completed** в согласованной границе. Закрыты общий overlap/expiry/revocation/retirement для legacy registry и dedicated TLS, точное сопоставление предъявленного сертификата и сохранение прежних legacy gates. [Результаты и границы проверки](04.09-credential-renewal/04.09-b3b1-accepted-leaf-policy.md#voter-credential-overlap-0409b3b1).
- Продолжение: отдельно назначить 04.09-B3b2 для сквозной привязки authenticated RPC к предъявленному credential, атомарной смены входящего и исходящего TLS/pools и восстановления по подтверждённой CredentialRevision. B3b остаётся открытой. Production admission закрыт.
- Чат: `threadId: 01a1ed04-6345-7613-8afb-b39887fbdb18`, `hostId: local`.
- Итоговый cursor: `13c67ad1-e7d9-44f7-aa78-5f42186907b7:41`.
- Модель нового чата: `gpt-6.2-sol`.

## Завершённое назначение B3a

- Обозначение: `NUI-04-20261118-07`.
- Объём: 04.09-B3a. Защищённый обмен запросами ротации через существующий peer framework, проверка стороны запроса и ответа.
- Состояние: **completed в изолированном объёме**. Существующий authenticated peer RPC передаёт B1 signing только между субъектом с прежним credential и принятым владельцем issuer epoch. Повтор использует сохранённые DER. Bind/Ready и production admission закрыты.
- Чат: `threadId: 01a1ecf3-7648-7dc3-9aa5-26d086a26e73`, `hostId: local`.
- Итоговый cursor: `9cf675d8-bded-4b15-a436-0512da92e986:54`.
- Модель нового чата: `gpt-6.2-sol`.
- Проверки: четыре отдельных compile-check без предупреждений и ошибок. MTP Web **83 успешно** (18 новых), Infrastructure **314 успешно**. В обоих наборах 0 ошибок и пропусков.
- Продолжение: координатору отдельно назначить B3b. Нужны legacy CRL/proxy, dedicated trust policy, overlap и смена TLS/pools с восстановлением. B3c и C остаются открытыми. Новых бизнес-вопросов и миграций нет. [Проверки и ограничения B3a](../../../Technical%20documentation/Testing.md#voter-credential-peer-0409b3a-2026-11-18).

## Завершённое назначение B2b2

- Обозначение: `NUI-04-20261118-06`.
- Объём: 04.09-B2b2, долговременная локальная установка принятого credential и восстановление после сбоя.
- Состояние: **completed в изолированном объёме**. Manifest, установленная ссылка и подтверждение сверяются с исходным материалом, exact Bind и fresh accepted cut. Старый материал сохранён. Ready/common и production admission закрыты.
- Чат: `threadId: 01a1ece3-336c-7bf2-a87e-355e240d362e`, `hostId: local`.
- Итоговый cursor: `60b75d0e-daaa-497c-a9ff-987195f716fa:47`.
- Модель нового чата: `gpt-6.2-sol` по указанию пользователя.
- Проверки: четыре compile-check без предупреждений/ошибок. MTP Infrastructure 296 (35 новых), Application 76, оба без ошибок/пропусков. [Команды, матрица и ограничения](../../../Technical%20documentation/Testing.md#voter-credential-local-installation-0409b2b2-2026-11-18).
- Продолжение: координатору отдельно назначить B3 — authenticated wire, legacy/dedicated trust и TLS/pools, scheduler/взаимная проверка/retirement с пределом overlap. Затем C. Действующей паузы и нерешённых бизнес-вопросов для B2b2 нет. Миграций нет. UI-приёмка, SQL, выборы, TLS и Ubuntu ACL/fsync остаются открытыми.

## Завершённое назначение B2b1

- Обозначение: `NUI-04-20261118-05`.
- Объём после оценки исполнителя: только 04.09-B2b1 — durable Bind operation intent, exact accepted Bind/CredentialRevision и recovery до сохранённого Bind result без signer после Bound/retirement. Compile-check, изолированная fault/replay/concurrency матрица и документация входят. Manifest/ref/install/confirmation и их recovery — отдельная B2b2. Runtime TLS switch, HTTP/wire, scheduler/retirement orchestration и ContinueCredential не входят. B2b/B2/04.09 остаются `in-progress`.
- Состояние: **B2b1 completed в ограниченной границе исполнителя**, финальные статические проверки и документация закончены после команды «Продолжай». Production C# и логика тестов не менялись; после AAA-комментариев в двух тестовых файлах выполнен дополнительный compile-check Infrastructure.Tests (0 предупреждений/ошибок). Четыре исходных compile-check и Infrastructure 261/Application 76 — результаты до паузы, не новые прогоны. B2b/B2/04.09 открыты. Координатор продолжает по отчёту без независимых проверок; production admission закрыт.
- Чат B2b: `threadId: 01a1eca4-3603-7871-b4fc-68faad903aac`, `hostId: local`.
- Исторический cursor остановленного хода: `dc37f85f-e738-412f-bd2c-6c0b56b4498f:47`. Итоговый cursor завершения после возобновления: `54ea0ae3-f121-462c-afb3-f084257d93b9:26`.
- Продолжение: после достаточного отчёта назначить только B2b2 отдельно, затем B3/C по зависимостям. Production admission остаётся закрытым до готовности authority, установки и trust.

## Результаты заданий

| Задание | Подэтапы | Результат и источник |
| --- | --- | --- |
| NUI-04-20261117-01 | 04.08 | Выполнено. Typed follower ingress/leader coordinator/remote signer, exact template/barrier/epoch, lock/journal до ответа; outage/lost response/смена leader/concurrent IssueId сохраняют identity/DER без обхода Bind. Remote grants сохраняют исходный intent/proof, signature без accepted record/consumption authority не даёт; исправлен caller replay при росте cut. Финальные Web 150 (58 routing +92 regression) и Infrastructure 15 успешно, 0 ошибок/пропусков; четыре compile-check — 0 предупреждений/ошибок. [Результат 04.08](04.08-remote-issuer-routing.md), [команды/доказательства](../../../Technical%20documentation/Testing.md#удалённый-issuer-и-follower-routing-0408-2026-11-17). |
| NUI-04-20261117-02 | 04.09-A | После паузы 17 ноября возобновлено и выполнено 18 ноября. Accepted rotation state/Authorize/Bind/Retire/CredentialRevision, expiry/overlap/replay, strict codec, snapshot/recovery ancestors, historical DER и безопасный audit проверены. Production admission закрыт до B. Application 124 (76 новых +48 regression) и Infrastructure 18 (13 новых +5 regression) успешно, 0 ошибок/пропусков; четыре compile-check — 0 предупреждений/ошибок. [Результат A и история паузы](04.09-leaf-renewal-and-overlap.md#фактический-результат), [команды/границы](../../../Technical%20documentation/Testing.md#voter-credential-rotation-0409a-2026-11-18). B/C не начаты, родитель 04.09 открыт. |
| NUI-04-20261118-03 | 04.09-B1 | Выполнен изолированный authority/signing core: отдельный old-key PoP/new CSR/current voter/revision/process/membership, nonce, exact accepted Authorize/majority, typed local/remote signer и прежний durable DER journal/issuer lock. Проверены altered inputs/response, revoked/expired/retired old leaf/issuer, отсутствие majority/issuer, lost response/unknown commit/смена term и реальная конкуренция на lock без нового DER/identity/Bind. **96 новых B1**, итоговый **Infrastructure 123** (96+27 regression), **Application 76 regression** успешно; в обоих 0 ошибок/пропусков. Четыре compile-check — 0 предупреждений/ошибок. Production DI/routes/dispatcher закрыты. [Результат/границы](04.09-leaf-renewal-and-overlap.md#фактический-результат), [команды и доказательства](../../../Technical%20documentation/Testing.md#voter-credential-authority-0409b1-2026-11-18). |
| NUI-04-20261118-04 | 04.09-B2a | Выполнено только ДО Bind: атомарный защищённый intent/new key/CSR/IDs/template/references, exact accepted authorization/public issuer и immutable DER pin. Повтор/reopen/fault/concurrency/altered inputs/missing/corrupt key/CSR/intent/DER/revision/fresh authority loss проверены. Offline read после synthetic Bind/retirement не вызывает signer; install/Ready не заявлены. **62 новых B2a**, итоговый **Infrastructure 187** (62+125 regression), **Application 76 regression** успешно; в обоих 0 ошибок/пропусков. Четыре compile-check без предупреждений/ошибок. [Результат/продолжение B2b](04.09-leaf-renewal-and-overlap.md#фактический-результат), [доказательства/команды](../../../Technical%20documentation/Testing.md#voter-credential-local-prebind-0409b2a-2026-11-18). |
| NUI-04-20261118-05 | 04.09-B2b1 | **Выполнено только до Bind evidence/result/recovery.** Durable immutable Bind intent предшествует Send; exact accepted Bind/result/index/CredentialRevision подтверждается fresh reconciliation, restart/lost response не вызывают signer после Bound/retirement. До паузы: четыре compile-check — 0 предупреждений/ошибок; Infrastructure **261 успешно** (74 новых B2b1 +187 regression), Application **76 regression успешно**, оба 0 ошибок/пропусков. После «Продолжай» завершены UTF-8/EOL/размеры/стиль/ссылки/solution items и согласование документов/Testing; production C# и логика тестов не менялись; уточнены AAA-комментарии двух тестовых файлов, дополнительный compile-check Infrastructure.Tests — 0 предупреждений/ошибок; MTP не повторялся. [Результат и история паузы](04.09-leaf-renewal-and-overlap.md#фактический-результат), [команды/доказательства](../../../Technical%20documentation/Testing.md#voter-credential-local-bind-0409b2b1-2026-11-18). B2b2 не начата, весь B2b/B2/04.09 открыт. |

Чат завершённого задания NUI-04-20261117-01: `threadId: 01a1ea13-780b-74a0-82b6-2b340a6c5ec2`, `hostId: local`; итоговый cursor `92ba88ac-f620-410c-8c48-bd661558c5b0:94`. Результат принят координатором только по отчёту, без независимого ревью кода.

Чат завершённого задания NUI-04-20261117-02: `threadId: 01a1ea35-42b2-7782-bb07-78bf85da95d4`, `hostId: local`; итоговый cursor после возобновления `1ed8fffb-9751-4160-b6b8-39e68ef4d8f4:71`. Координатор продолжает по отчёту A, без повторной проверки кода.

Чат завершённого задания NUI-04-20261118-03: `threadId: 01a1ec64-30f4-7d81-b82f-aa7e3e213480`, `hostId: local`; итоговый cursor `b295355c-81a1-49e0-a3da-b6b44c532be9:74`. B1 принята по отчёту в изолированной границе, без приписывания production/wire готовности.

Чат завершённого задания NUI-04-20261118-04: `threadId: 01a1ec85-21cc-7ac3-9218-83028126d604`, `hostId: local`; итоговый cursor `07262818-3f15-499d-857b-63a36a3222dd:60`. B2a принята только до Bind по отчёту, B2 остаётся открытой.

## Открытые границы

**История паузы 18 ноября:** по прямой просьбе пользователя B2b1 остановлена после завершённых compile-check/тестов до финальных статических проверок/согласования документов. На паузе completed не заявлялся; сохранены код и все прежние правки. Позднейшая команда пользователя «Продолжай» сняла паузу только для завершения B2b1 с этой точки. Историческая подробная запись сохранена в 04.09; теперь финальная статическая/документационная граница закрыта. После возобновления production C# и логика тестов не менялись; уточнены только AAA-комментарии в двух тестовых файлах с дополнительным успешным compile-check Infrastructure.Tests. Прежние MTP-прогоны не повторялись.

**Доказанная граница B2b1:** реальные protected files/new lease/store и local crypto, memory accepted/reducer/barrier/transport/journal; Bind intent до Send, result по exact accepted evidence и повторному fresh cut. Manifest/ref/install/confirmation и installed revision не созданы. Старый материал и все прежние незакоммиченные изменения сохранены. Новых migrations нет; БД/приложение/реальная сеть/Git/Docker/Telegram/deployment не использовались. SQL/fsync/power-loss/второй OS-процесс/Ubuntu ACL/TLS/elections/сеть и UI-приёмка остаются 07.

**Точка передачи после B2b1 (история).** Следующим назначением стало B2b2. Его итог и актуальное продолжение указаны выше.

Открытых бизнес-вопросов нет. Оставшиеся технические зависимости записаны в [открытых вопросах](../open-questions.md#технические-вопросы-разработчику).

UI-приёмка и реальные Ubuntu/TLS/ACL/fsync/SQL/сетевые сценарии остаются за пользователем и этапом 07; изолированные тесты их не закрывают. Готовность полного Join и всего этапа 04 не заявлена.

В рамках 04.08 отрицательные проверки закрыли неверные origin/storage/scope/template/authorization index/issuer epoch/CSR/process/actor/versions/expiry, missing majority и revocation/retirement/maintenance. CatchUp/arbitrary Sign(bytes)/browser/nonvoter закрыты. Проверки RPC использовали synthetic accepted membership/local restart proof; они не закрывают полный lifecycle последующих voters 04.12/04.13, replacement 04.11 или E5–E8 этапа 05. История A/04.08 сохранена; результаты B1 приведены отдельно выше.

**Закрыто B1:** проверяемые контракты/PoP/CSR, exact accepted authorization, fresh barrier/проверка ответа, local и подставной remote signing, immutable DER journal/replay/конкуренция, отсутствие неявного Bind/Ready и сохранённый production deny. **Открыто:** production wire/DI/admission, legacy CRL/proxy integration (B1 old-leaf crypto использует dedicated EnrollmentLiveTrust), post-Bind voter key install/recovery B2b, trust/TLS/scheduler/retirement B3, ContinueCredential C. Post-Bind signing replay закрыт; восстановление установки не должно повторно выпускать leaf. Second-rotation test использует synthetic accepted Bind/retirement/process lease и не подтверждает local installation. Native WAL ротации, реальные elections/majority/RPC/SQL durability/fsync не проверены. UI-приёмка ожидает пользователя в 07.

**Закрыто B2a:** protected intent/key/CSR/template/DER и exact Authorize. **Закрыто B2b1:** durable Bind intent/result и exact accepted recovery. **Закрыто B2b2:** локальная CredentialRevision и подтверждение с проверкой material/Bind/cut после каждой записи. Synthetic retirement проверяет recovery без signer. Для согласованного отката всех файлов и accepted evidence нужен независимый доверенный источник. Физические SQL/fsync/Ubuntu ACL/TLS/сеть и приёмка остаются в 07.

Миграций B1/B2 нет. БД/сеть/приложение/хост/Git/Docker/Telegram/deployment не использовались. Исполнитель завершил B2b2 и остановился. B3/C, 04.10–04.18, 04.09 в целом и этап 04 открыты. Локальный коммит разрешён только при полном завершении этапа, push запрещён.
