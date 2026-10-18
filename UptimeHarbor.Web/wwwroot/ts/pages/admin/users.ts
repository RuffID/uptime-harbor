import { createDataTable, createTableAction, matchesSearch } from "../../shared/dataTable.js";
import { renderHelpTooltip } from "../../shared/helpTooltip.js";
import { createSearchableRadioSelect } from "../../shared/searchableRadioSelect.js";
import {
    ApiError,
    sendJsonRequest,
    ServiceResult,
    ServiceResultWithData,
    unwrapServiceResult,
    unwrapServiceSuccess
} from "../../shared/http.js";

interface UserAccount {
    id: string;
    login: string;
    isEnabled: boolean;
    isAdministrator: boolean;
    serverIds: string[];
    containerIds: string[];
}

interface Server { id: string; name: string; endpointUrl: string; lastKnownName: string | null; }
interface Container { id: string; serverId: string | null; endpointUrl: string; lastKnownName: string | null; }
interface ServerWithContainers extends Server { containers: Container[]; }
interface BootstrapModalInstance { show(): void; hide(): void; }
interface BootstrapWindow extends Window {
    bootstrap?: { Modal: { getOrCreateInstance(element: Element): BootstrapModalInstance } };
}

const state: { users: UserAccount[]; servers: ServerWithContainers[]; selectedUserId: string | null; saving: boolean } = {
    users: [], servers: [], selectedUserId: null, saving: false
};

const page = requireElement<HTMLElement>("[data-users-page]");
const pageError = requireElement<HTMLElement>("[data-page-error]");
const pageSuccess = requireElement<HTMLElement>("[data-page-success]");
const userList = requireElement<HTMLElement>("[data-user-list]");
const userEmpty = requireElement<HTMLElement>("[data-user-empty]");
const editModal = requireElement<HTMLElement>("[data-edit-modal]");
const editForm = requireElement<HTMLFormElement>("[data-edit-form]");
const editError = requireElement<HTMLElement>("[data-edit-error]");
const createModal = requireElement<HTMLElement>("[data-create-modal]");
const createForm = requireElement<HTMLFormElement>("[data-create-form]");
const createLogin = requireElement<HTMLInputElement>("[data-create-login]");
const createPassword = requireElement<HTMLInputElement>("[data-create-password]");
const createGeneralError = requireElement<HTMLElement>("[data-create-general-error]");
const userTitle = requireElement<HTMLElement>("[data-user-title]");
const userRole = requireElement<HTMLElement>("[data-user-role]");
const userEnabled = requireElement<HTMLInputElement>("[data-user-enabled]");
const visibilityList = requireElement<HTMLElement>("[data-visibility-list]");
const currentUserId = document.querySelector<HTMLMetaElement>('meta[name="current-user-id"]')?.content ?? "";
const userSearch = requireElement<HTMLInputElement>("[data-user-search]");
const userStateFilter = requireElement<HTMLSelectElement>("[data-user-state-filter]");
let usersTable: ReturnType<typeof createDataTable>;
let visibilityTable: ReturnType<typeof createDataTable>;
const temporarilyDisabled = new Set<HTMLButtonElement | HTMLInputElement | HTMLSelectElement>();

document.addEventListener("DOMContentLoaded", () => { void initUsersPage(); });

async function initUsersPage(): Promise<void> {
    createSearchableRadioSelect(requireElement<HTMLSelectElement>("[data-user-state-filter]"));
    usersTable = createDataTable(userList, "users", "Учётные записи", [
        { key: "login", title: "Логин", width: 240 },
        { key: "role", title: "Роль", width: 220, help: "Администратор управляет настройками; пользователю показываются разрешённые объекты мониторинга." },
        { key: "enabled", title: "Состояние", width: 170 },
        { key: "servers", title: "Серверы", width: 140, help: "Число явно разрешённых серверов. Администратор имеет полный доступ." },
        { key: "containers", title: "Контейнеры", width: 160, help: "Число явно разрешённых контейнеров. Администратор имеет полный доступ." },
        { key: "actions", title: "Действия", width: 140 }
    ]);
    visibilityTable = createDataTable(visibilityList, "visibility", "Доступ пользователя к объектам", [
        { key: "object", title: "Объект", width: 260 },
        { key: "kind", title: "Тип", width: 150 },
        { key: "server", title: "Сервер", width: 240 },
        { key: "access", title: "Доступ", width: 180, help: "Чтобы разрешить контейнер, сначала разрешите его сервер. Снятие доступа к серверу снимает выбор его контейнеров." }
    ]);
    renderHelpTooltip(requireElement<HTMLElement>("[data-visibility-help]"), {
        title: "Доступ к объектам",
        text: "Сначала разрешите сервер, затем нужные контейнеры. Изменения применяются кнопкой «Сохранить». Администратор видит все объекты независимо от списка разрешений."
    });
    userSearch.addEventListener("input", renderUsersTable);
    userStateFilter.addEventListener("change", renderUsersTable);
    page.addEventListener("click", handleClick);
    createForm.addEventListener("submit", event => void createUser(event));
    createModal.addEventListener("show.bs.modal", resetCreateForm);
    createModal.addEventListener("shown.bs.modal", () => createLogin.focus());
    editForm.addEventListener("submit", event => {
        event.preventDefault();
        void runEditCommand(saveVisibility);
    });
    editModal.addEventListener("shown.bs.modal", () => {
        visibilityTable.refresh();
        userTitle.focus();
    });
    editModal.addEventListener("hide.bs.modal", event => {
        if (state.saving) event.preventDefault();
    });
    editModal.addEventListener("hidden.bs.modal", () => {
        clearEditError();
        const trigger = Array.from(userList.querySelectorAll<HTMLButtonElement>('[data-action="select-user"]'))
            .find(button => button.dataset.userId === state.selectedUserId);
        (trigger ?? userSearch).focus();
    });
    await runCommand(loadData);
}

async function loadData(): Promise<void> {
    const usersResponse = await sendJsonRequest<ServiceResultWithData<UserAccount[]>>("/api/admin/users");
    state.users = unwrapServiceResult(usersResponse);
    const serversResponse = await sendJsonRequest<ServiceResultWithData<Server[]>>("/api/admin/objects/servers");
    const servers = unwrapServiceResult(serversResponse);
    state.servers = await Promise.all(servers.map(async server => {
        const response = await sendJsonRequest<ServiceResultWithData<Container[]>>(`/api/admin/objects/servers/${server.id}/containers`);
        return { ...server, containers: unwrapServiceResult(response) };
    }));
    if (state.selectedUserId !== null && !state.users.some(user => user.id === state.selectedUserId)) state.selectedUserId = null;
    render();
}

function renderUsersTable(): void {
    const users = state.users.filter(user => matchesSearch(userSearch.value, user.login)
        && (userStateFilter.value === "" || user.isEnabled === (userStateFilter.value === "enabled")));
    const rows = users.map(user => {
        const button = createTableAction("Изменить");
        button.dataset.action = "select-user";
        button.dataset.userId = user.id;
        const status = document.createElement("span");
        status.classList.add("badge", user.isEnabled ? "text-bg-success" : "text-bg-secondary");
        status.textContent = user.isEnabled ? "Включён" : "Отключён";
        return usersTable.row([
            user.login, user.isAdministrator ? "Администратор" : "Пользователь", status,
            user.isAdministrator ? "Все" : String(user.serverIds.length),
            user.isAdministrator ? "Все" : String(user.containerIds.length), button
        ], user.id === state.selectedUserId);
    });
    usersTable.body.replaceChildren(...rows);
    userEmpty.classList.toggle("d-none", users.length !== 0);
    userEmpty.textContent = state.users.length === 0 ? "Пользователей пока нет." : "По выбранным фильтрам пользователи не найдены.";
    usersTable.refresh();
}

function render(): void {
    renderUsersTable();
    const selected = state.selectedUserId === null ? null : requireUser(state.selectedUserId);
    if (selected) {
        userTitle.textContent = `Изменить пользователя: ${selected.login}`;
        userRole.textContent = selected.isAdministrator ? "Администратор" : "Пользователь";
        userRole.classList.remove("text-bg-info", "text-bg-secondary");
        userRole.classList.add(selected.isAdministrator ? "text-bg-info" : "text-bg-secondary");
        userEnabled.checked = selected.isEnabled;
        userEnabled.disabled = selected.isAdministrator || selected.id === currentUserId;
        requireElement<HTMLElement>('[data-action="delete-user"]').classList.toggle("d-none", selected.isAdministrator || selected.id === currentUserId);
        renderVisibility(selected);
    }
    setSaving(state.saving);
}

function renderVisibility(user: UserAccount): void {
    const rows: HTMLTableRowElement[] = [];
    for (const server of state.servers) {
        const serverInput = document.createElement("input");
        serverInput.type = "checkbox"; serverInput.id = `server_${server.id}`; serverInput.name = "serverIds"; serverInput.value = server.id;
        serverInput.checked = user.serverIds.includes(server.id); serverInput.dataset.serverId = server.id; serverInput.classList.add("form-check-input");
        const serverLabel = document.createElement("label"); serverLabel.htmlFor = serverInput.id; serverLabel.classList.add("form-check-label", "fw-semibold");
        serverLabel.textContent = "Разрешить";
        const serverAccess = document.createElement("div"); serverAccess.classList.add("form-check"); serverAccess.append(serverInput, serverLabel);
        rows.push(visibilityTable.row([server.name, "Сервер", "—", serverAccess]));
        const containerInputs: HTMLInputElement[] = [];
        for (const container of server.containers) {
            const access = document.createElement("div"); access.classList.add("form-check");
            const input = document.createElement("input"); input.type = "checkbox"; input.id = `container_${container.id}`; input.name = "containerIds"; input.value = container.id; input.dataset.parentServerId = server.id; input.checked = user.containerIds.includes(container.id); input.disabled = !serverInput.checked; input.classList.add("form-check-input");
            const label = document.createElement("label"); label.htmlFor = input.id; label.classList.add("form-check-label"); label.textContent = "Разрешить";
            access.append(input, label); containerInputs.push(input);
            rows.push(visibilityTable.row([container.lastKnownName ?? container.endpointUrl, "Контейнер", server.name, access]));
        }
        serverInput.addEventListener("change", () => {
            for (const input of containerInputs) {
                input.disabled = !serverInput.checked;
                if (!serverInput.checked) input.checked = false;
            }
        });
    }
    visibilityTable.body.replaceChildren(...rows);
    visibilityTable.refresh();
}

async function createUser(event: SubmitEvent): Promise<void> {
    event.preventDefault();
    await runSavingCommand(async () => {
        clearFieldErrors();
        hideCreateGeneralError();
        try {
            const response = await sendJsonRequest<ServiceResultWithData<UserAccount>>("/api/admin/users", "POST", { login: createLogin.value, initialPassword: createPassword.value });
            const created = unwrapServiceResult(response);
            state.selectedUserId = created.id;
            hideModal(createModal);
            showSuccess("Пользователь создан.");
            await loadData();
        }
        catch (error: unknown) {
            if (!showCreateFieldError(error)) {
                showCreateGeneralError(error);
            }
        }
    });
}

async function saveVisibility(): Promise<void> {
    const user = requireSelectedUser();
    await runSavingCommand(async () => {
        const serverIds = Array.from(visibilityList.querySelectorAll<HTMLInputElement>('input[name="serverIds"]:checked')).map(input => input.value);
        const containerIds = Array.from(visibilityList.querySelectorAll<HTMLInputElement>('input[name="containerIds"]:checked')).map(input => input.value);
        if (userEnabled.checked !== user.isEnabled) {
            const enabledResponse = await sendJsonRequest<ServiceResult>(`/api/admin/users/${user.id}/enabled`, "PUT", { isEnabled: userEnabled.checked });
            unwrapServiceSuccess(enabledResponse);
        }
        const visibilityResponse = await sendJsonRequest<ServiceResult>(`/api/admin/users/${user.id}/visibility`, "PUT", { serverIds, containerIds });
        unwrapServiceSuccess(visibilityResponse);
        await loadData();
    });
    hideModal(editModal);
    showSuccess("Изменения сохранены.");
}

async function deleteUser(): Promise<void> {
    const user = requireSelectedUser();
    if (user.isAdministrator || user.id === currentUserId) throw new Error("Эту учётную запись нельзя удалить.");
    if (!window.confirm(`Удалить пользователя «${user.login}» и его права?`)) return;
    await runSavingCommand(async () => { const response = await sendJsonRequest<ServiceResult>(`/api/admin/users/${user.id}`, "DELETE"); unwrapServiceSuccess(response); state.selectedUserId = null; await loadData(); });
    hideModal(editModal);
    showSuccess("Пользователь удалён.");
}

function handleClick(event: MouseEvent): void {
    const target = event.target; if (!(target instanceof Element)) return;
    const element = target.closest<HTMLElement>("[data-action]"); const action = element?.dataset.action; if (!action) return;
    if (action === "select-user") {
        state.selectedUserId = requireData(element, "userId");
        clearPageError();
        clearEditError();
        render();
        const bootstrapApi = (window as BootstrapWindow).bootstrap;
        if (!bootstrapApi) throw new Error("Bootstrap не загружен.");
        bootstrapApi.Modal.getOrCreateInstance(editModal).show();
    }
    else if (action === "delete-user") void runEditCommand(deleteUser);
    else throw new Error(`Неизвестное действие: ${action}.`);
}

async function runCommand(command: () => Promise<void>): Promise<void> { clearPageError(); try { await command(); } catch (error: unknown) { showPageError(error); } }
async function runEditCommand(command: () => Promise<void>): Promise<void> {
    clearEditError();
    try { await command(); }
    catch (error: unknown) {
        editError.textContent = error instanceof Error ? error.message : "Не удалось изменить пользователя.";
        editError.classList.remove("d-none");
        visibilityTable.refresh();
    }
}
function clearEditError(): void { editError.textContent = ""; editError.classList.add("d-none"); }
async function runSavingCommand(command: () => Promise<void>): Promise<void> { state.saving = true; setSaving(true); try { await command(); } finally { state.saving = false; setSaving(false); } }
function setSaving(saving: boolean): void {
    if (saving) {
        for (const element of page.querySelectorAll<HTMLButtonElement | HTMLInputElement | HTMLSelectElement>("button, input, select")) {
            if (!element.disabled) temporarilyDisabled.add(element);
            element.disabled = true;
        }
    }
    else {
        for (const element of temporarilyDisabled) element.disabled = false;
        temporarilyDisabled.clear();
    }
}
function showCreateFieldError(error: unknown): boolean { if (!(error instanceof ApiError) || !error.field) return false; const input = createForm.querySelector<HTMLInputElement>(`[name="${error.field}"]`); const message = createForm.querySelector<HTMLElement>(`[data-create-error="${error.field}"]`); if (!input || !message) return false; input.classList.add("is-invalid"); message.textContent = error.message; return true; }
function clearFieldErrors(): void { for (const input of createForm.querySelectorAll<HTMLElement>(".is-invalid")) input.classList.remove("is-invalid"); for (const message of createForm.querySelectorAll<HTMLElement>(".invalid-feedback")) message.textContent = ""; }
function resetCreateForm(): void { createForm.reset(); clearFieldErrors(); hideCreateGeneralError(); }
function showCreateGeneralError(error: unknown): void { createGeneralError.textContent = error instanceof Error ? error.message : "Не удалось создать пользователя."; createGeneralError.classList.remove("d-none"); }
function hideCreateGeneralError(): void { createGeneralError.textContent = ""; createGeneralError.classList.add("d-none"); }
function hideModal(element: Element): void { const bootstrapApi = (window as BootstrapWindow).bootstrap; if (!bootstrapApi) throw new Error("Bootstrap не загружен."); bootstrapApi.Modal.getOrCreateInstance(element).hide(); }
function showPageError(error: unknown): void { pageError.textContent = error instanceof Error ? error.message : "Неизвестная ошибка."; pageError.classList.remove("d-none"); }
function clearPageError(): void { pageError.textContent = ""; pageError.classList.add("d-none"); }
function showSuccess(message: string): void { pageSuccess.textContent = message; pageSuccess.classList.remove("d-none"); }
function requireUser(id: string): UserAccount { const user = state.users.find(item => item.id === id); if (!user) throw new Error("Пользователь не найден."); return user; }
function requireSelectedUser(): UserAccount { if (!state.selectedUserId) throw new Error("Пользователь не выбран."); return requireUser(state.selectedUserId); }
function requireData(element: HTMLElement, name: string): string { const value = element.dataset[name]; if (!value) throw new Error(`Отсутствует data-${name}.`); return value; }
function requireElement<T extends Element>(selector: string): T { const element = document.querySelector<T>(selector); if (!element) throw new Error(`Обязательный элемент не найден: ${selector}.`); return element; }
