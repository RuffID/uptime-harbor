import { CommandDraft } from "./clusterDraft.js";
import { element, button, kindNames, operationText } from "./clusterDom.js";
import type { AccountChange, Change, Entity, EntityKind, Pending, Conflict, Settings, Snapshot, Operation } from "./clusterModels.js";

interface EditorContext {
    snapshot(): Snapshot;
    submit(body: Change | AccountChange, account: boolean): Promise<Operation>;
    refresh(): Promise<void>;
    failure(error: unknown): void;
    show(): void;
    hide(): void;
}

export class ClusterEditor {
    private closeCurrent: (() => void) | null = null;
    private busy: () => boolean = () => false;
    private host: HTMLElement;
    private context: EditorContext;
    constructor(host: HTMLElement, context: EditorContext) { this.host = host; this.context = context; }

    get isSaving(): boolean { return this.busy(); }
    close(): void {
        if (!this.closeCurrent) return;
        this.closeCurrent(); this.closeCurrent = null; this.busy = () => false;
        this.host.replaceChildren(); this.context.hide();
    }

    open(kind: EntityKind, entity: Entity | null, action = "Upsert", pending: Pending | null = null,
        conflict: Conflict | null = null, useCurrent = false): void {
        this.close();
        const snapshot = this.context.snapshot();
        const entityId = pending?.entityId ?? conflict?.entityId ?? entity?.id ?? crypto.randomUUID();
        let baseRevision = action === "TestDelivery" ? 0 : snapshot.entities.find(item => item.id === entityId)?.revision ?? 0;
        let operationId = crypto.randomUUID();
        let controlId = pending ? crypto.randomUUID() : null;
        let alive = true;
        let saving = false;
        this.busy = () => saving;
        let attempted = false;
        const draft = new CommandDraft<Change | AccountChange>();
        const source = useCurrent ? conflict?.current?.settings : pending?.settings ?? conflict?.proposed ?? entity?.settings;
        const form = element("form", "", "card card-body gap-3");
        const title = element("h2", "", "h5"); title.id = "cluster-editor-title"; title.tabIndex = -1;
        const information = element("p", "", "small text-body-secondary mb-0");
        const result = element("p", "", "mb-0"); result.setAttribute("role", "status");
        const error = element("p", "", "text-danger mb-0"); error.setAttribute("role", "alert");
        const fields = element("fieldset", "", "d-flex flex-column gap-3");
        const inputs = new Map<string, HTMLInputElement | HTMLSelectElement>();
        const add = (name: string, caption: string, type: string, value: string): HTMLInputElement => {
            const label = element("label", caption, "form-label mb-0");
            const input = element("input", "", "form-control");
            input.name = name; input.type = type; input.value = value;
            input.id = `cluster-edit-${name}`; label.htmlFor = input.id;
            if (type === "password") input.autocomplete = "new-password";
            if (type === "text") input.maxLength = 2048;
            label.append(input); fields.append(label); inputs.set(name, input);
            return input;
        };
        const check = (name: string, caption: string, checked: boolean): HTMLInputElement => {
            const label = element("label", "", "form-check");
            const input = element("input", "", "form-check-input"); input.type = "checkbox";
            input.name = name; input.checked = checked;
            input.id = `cluster-edit-${name}`; label.htmlFor = input.id;
            label.append(input, document.createTextNode(caption)); fields.append(label); inputs.set(name, input);
            return input;
        };
        const select = (name: string, caption: string, options: [string, string][], value: string, multiple = false): HTMLSelectElement => {
            const label = element("label", caption, "form-label mb-0");
            const input = element("select", "", "form-select"); input.name = name; input.multiple = multiple;
            input.id = `cluster-edit-${name}`; label.htmlFor = input.id;
            for (const [id, text] of options) {
                const option = element("option", text); option.value = id; option.selected = id === value;
                input.append(option);
            }
            label.append(input); fields.append(label); inputs.set(name, input);
            return input;
        };
        const acceptedOptions = (kinds: EntityKind[]): [string, string][] => snapshot.entities
            .filter(item => !item.deleted && kinds.includes(item.kind))
            .map(item => [item.id, `${item.settings.name ?? kindNames[item.kind]} · ${item.id}`]);
        const isAccount = kind === "Account";
        let accountAction = pending?.accountAction ?? conflict?.accountAction ?? (entity ? "Update" : "Create");
        if (isAccount && action !== "Upsert") accountAction = action;
        if (isAccount && useCurrent) accountAction = "Update";
        if (isAccount) {
            form.append(element("p", "Единственная роль — Administrator. Все администраторы управляют всеми ресурсами. Self-disable и self-delete запрещены; последний активный администратор сохраняется.", "small mb-0"));
            if (accountAction === "Create" || accountAction === "Update") add("name", "Логин", "text", source?.name ?? "").maxLength = 256;
            if (accountAction === "Create" || accountAction === "Password") add("password", "Новый пароль (для восстановленной/конфликтной заявки введите заново)", "password", "");
            if (accountAction === "SetEnabled") check("enabled", "Учётная запись включена", source?.enabled ?? true);
        }
        else if (action === "Upsert") {
            if (kind === "Server" || kind === "Container") {
                add("name", "Название конфигурации (metadata каждого observer показаны отдельно)", "text", source?.name ?? "");
                add("endpoint", "HTTP endpoint", "url", source?.endpoint ?? "");
                add("description", "Описание", "text", source?.description ?? "");
                check("enabled", "Мониторинг включён", source?.enabled ?? true);
                if (kind === "Container") select("parentId", "Сервер", [["", "Без сервера"], ...acceptedOptions(["Server"])], source?.parentId ?? "");
                const observers = select("observers", "Назначенные observers — NodeId (возможен множественный выбор)", snapshot.nodes.map(id => [id, id]), "", true);
                for (const option of observers.options) option.selected = source?.observerNodes.includes(option.value) ?? false;
                add("failureThreshold", "Порог неудач", "number", String(source?.failureThreshold ?? 3)).min = "1";
                add("intervalSeconds", "Интервал, секунды", "number", String(source?.intervalSeconds ?? 60)).min = "1";
                add("timeoutSeconds", "Timeout, секунды", "number", String(source?.timeoutSeconds ?? 10)).min = "1";
            }
            else if (kind === "Chat") {
                add("name", "Название чата", "text", source?.name ?? "");
                add("telegramChatId", "Telegram Chat ID", "text", String(source?.telegramChatId ?? ""));
            }
            else {
                select("chatId", "Чат", [["", "Выберите чат"], ...acceptedOptions(["Chat"])], source?.chatId ?? "");
                if (kind === "Binding") {
                    select("parentId", "Цель", [["", "Выберите цель"], ...acceptedOptions(["Server", "Container"])], source?.parentId ?? "");
                    form.append(element("p", "Новая привязка получает текущую открытую проблему. При снятии привязки не начатый outage отменяется; после успешного outage сохраняется один заключительный recovery, включая поздний успех уже начатой отправки.", "small mb-0"));
                }
                else {
                    select("parentId", "Отдельный маршрут для NodeId", [["", "Выберите узел"], ...snapshot.nodes.map(id => [id, id] as [string, string])], source?.parentId ?? "");
                    check("enabled", "Маршрут включён", source?.enabled ?? true);
                }
            }
        }
        if (action === "Delete") {
            form.append(element("p", "Удаление глобальное: после принятия объект удаляется во всей сетке. Проверьте контейнеры, привязки и маршруты; зависимости проверяются при принятии. Новые отправки удалённой цели/чата прекращаются, уже начатый запрос может завершиться.", "text-danger mb-0"));
            const dependencies = snapshot.entities.filter(item => !item.deleted && (item.settings.parentId === entityId || item.settings.chatId === entityId));
            form.append(element("p", `Зависимости в принятой конфигурации (${dependencies.length}): ${dependencies.map(item => `${kindNames[item.kind]} · ${item.settings.name ?? item.id}`).join("; ") || "нет"}.`, "small mb-0"));
            check("globalDelete", "Подтверждаю глобальное удаление", false).required = true;
        }
        if (action === "TestDelivery") form.append(element("p", "Тест подаётся в принятую очередь главного узла. HTTP 202 означает «в очереди», доставка подтверждается отдельным результатом. Готовность отправителя может быть неизвестна.", "small mb-0"));
        if (pending?.state === "NeedsUserConfirmation") check("restored", "Подтверждаю новую заявку от актуальной ревизии после сверки известных исходов. Удержанная заявка автоматически не выпускается.", false).required = true;
        const read = (name: string): string => inputs.get(name)?.value ?? "";
        const checked = (name: string): boolean => (inputs.get(name) as HTMLInputElement | undefined)?.checked ?? false;
        const nullable = (name: string): string | null => read(name) || null;
        const refreshTitle = (): void => {
            title.textContent = `${kindNames[kind]} · ${isAccount ? accountAction : action}`;
            information.textContent = `EntityId: ${entityId}. Base revision: ${baseRevision}. OperationId: ${operationId}${controlId ? `. Control: ${controlId}` : ""}. Принятые настройки не изменятся до commit.`;
        };
        const actions = element("div", "", "d-flex flex-wrap gap-2");
        const submit = element("button", "Подать / повторить ту же заявку", "btn btn-primary"); submit.type = "submit";
        const rebase = button("Новая команда от актуальной ревизии", () => { void rebaseDraft(); });
        const close = button("Закрыть редактор", () => this.close());
        const rebaseDraft = async (): Promise<void> => {
            if (attempted && !window.confirm("Предыдущая операция могла быть принята и продолжает обрабатываться. Сначала проверьте её результат. Создать новую независимую команду, сохранив ввод?")) return;
            try {
                await this.context.refresh();
                if (!alive) return;
                baseRevision = action === "TestDelivery" ? 0 : this.context.snapshot().entities.find(item => item.id === entityId)?.revision ?? 0;
                operationId = crypto.randomUUID(); controlId = pending ? crypto.randomUUID() : null;
                draft.clear(); attempted = false;
                const held = inputs.get("restored") as HTMLInputElement | undefined;
                if (held) held.checked = false;
                result.textContent = "Ввод сохранён; подготовлена новая команда от актуальной ревизии.";
                error.textContent = ""; refreshTitle();
            }
            catch (failure: unknown) { this.context.failure(failure); }
        };
        actions.append(submit, rebase, close); form.prepend(title, information); form.append(fields, actions, result, error);
        form.addEventListener("submit", event => {
            event.preventDefault();
            if (saving || !form.reportValidity()) return;
            void send();
        });
        const send = async (): Promise<void> => {
            error.textContent = "";
            try {
                if ((action === "Delete") && !checked("globalDelete")) throw new Error("Подтвердите глобальное удаление.");
                if (pending?.state === "NeedsUserConfirmation" && !checked("restored")) throw new Error("Подтвердите новую заявку после восстановления.");
                const shared = { operationId, baseRevision, resolvesConflictId: conflict?.id ?? null,
                    targetOperationId: pending?.operationId ?? null, controlOperationId: controlId, confirmRestored: checked("restored") };
                let body: Change | AccountChange;
                if (isAccount) {
                    body = { ...shared, userId: entityId, action: accountAction,
                        login: accountAction === "Create" || accountAction === "Update" ? read("name") : null,
                        password: accountAction === "Create" || accountAction === "Password" ? read("password") : null,
                        isEnabled: accountAction === "SetEnabled" ? checked("enabled") : null };
                }
                else {
                    let settings: Settings | null = null;
                    if (action === "Upsert") {
                        const observerSelect = inputs.get("observers") as HTMLSelectElement | undefined;
                        settings = { name: nullable("name"), endpoint: nullable("endpoint"), description: nullable("description"),
                            enabled: kind === "Binding" ? snapshot.entities.find(item => item.id === read("parentId"))?.kind === "Server" : inputs.has("enabled") ? checked("enabled") : true,
                            parentId: nullable("parentId"), chatId: nullable("chatId"), telegramChatId: Number(read("telegramChatId")),
                            observerNodes: observerSelect ? Array.from(observerSelect.selectedOptions, option => option.value) : [],
                            failureThreshold: Number(read("failureThreshold") || 3), intervalSeconds: Number(read("intervalSeconds") || 60), timeoutSeconds: Number(read("timeoutSeconds") || 10) };
                        if (!Number.isSafeInteger(settings.telegramChatId)) throw new Error("Chat ID должен быть целым числом без потери точности.");
                    }
                    body = { ...shared, entityId, kind, action, settings, confirmGlobalDelete: checked("globalDelete") };
                }
                const prepared = draft.prepare(body);
                attempted = true; saving = true; fields.disabled = true; submit.disabled = true; rebase.disabled = true; close.disabled = true;
                const outcome = await this.context.submit(prepared, isAccount);
                if (!alive) return;
                result.textContent = operationText(outcome);
                if (action === "TestDelivery") result.append(document.createTextNode(" Это результат команды очереди, а не подтверждение доставки Telegram. Проверьте журнал доставок."));
                await this.context.refresh();
            }
            catch (failure: unknown) {
                if (alive) error.textContent = `${failure instanceof Error ? failure.message : "Запрос не подтверждён."} Ввод сохранён. Потеря ответа не означает отмену.`;
                this.context.failure(failure);
            }
            finally {
                saving = false;
                if (alive) { fields.disabled = false; submit.disabled = false; rebase.disabled = false; close.disabled = false; }
            }
        };
        this.closeCurrent = () => { alive = false; draft.clear(); form.reset(); };
        refreshTitle(); this.host.append(form); this.context.show(); title.focus();
    }
}
