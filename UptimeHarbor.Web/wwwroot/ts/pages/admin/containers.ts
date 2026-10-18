import { createDataTable, createTableAction, matchesSearch } from "../../shared/dataTable.js";
import { createSearchableRadioSelect } from "../../shared/searchableRadioSelect.js";
import { ApiError, sendJsonRequest, ServiceResult, ServiceResultWithData, unwrapServiceResult, unwrapServiceSuccess } from "../../shared/http.js";

interface Server { id: string; name: string; }
interface Container {
    id: string;
    serverId: string | null;
    endpointUrl: string;
    description: string | null;
    isMonitoringEnabled: boolean;
    lastKnownName: string | null;
    status: number;
    consecutiveFailures: number;
    version: number;
}
interface BootstrapWindow extends Window {
    bootstrap?: { Modal: { getOrCreateInstance(element: Element): { show(): void; hide(): void; } } };
}

const state = { servers: [] as Server[], containers: [] as Container[], selectedId: null as string | null, saving: false };
const page = requireElement<HTMLElement>("[data-containers-page]");
const pageError = requireElement<HTMLElement>("[data-page-error]");
const list = requireElement<HTMLElement>("[data-container-list]");
const empty = requireElement<HTMLElement>("[data-container-empty]");
const search = requireElement<HTMLInputElement>("[data-container-search]");
const status = requireElement<HTMLSelectElement>("[data-container-status-filter]");
const editModal = requireElement<HTMLElement>("[data-edit-container-modal]");
const createModal = requireElement<HTMLElement>("[data-create-container-modal]");
const editForm = requireElement<HTMLFormElement>("[data-container-form]");
const createForm = requireElement<HTMLFormElement>("[data-create-container-form]");
const editServer = requireElement<HTMLSelectElement>("[data-container-server]");
const createServer = requireElement<HTMLSelectElement>("[data-create-container-server]");
const editServerSelect = createSearchableRadioSelect(editServer, { noOptionsText: "Нет объектов." });
const createServerSelect = createSearchableRadioSelect(createServer, { noOptionsText: "Нет объектов." });
const temporarilyDisabled = new Set<HTMLButtonElement | HTMLInputElement | HTMLSelectElement | HTMLTextAreaElement>();
let table: ReturnType<typeof createDataTable>;

document.addEventListener("DOMContentLoaded", () => {
    void initContainersPage();
});

async function initContainersPage(): Promise<void> {
    table = createDataTable(list, "all-containers", "Контейнеры", [
        { key: "name", title: "Контейнер", width: 220 },
        { key: "server", title: "Сервер", width: 220 },
        { key: "endpoint", title: "Адрес проверки", width: 280 },
        { key: "status", title: "Статус", width: 150 },
        { key: "enabled", title: "Мониторинг", width: 150 },
        { key: "failures", title: "Сбои подряд", width: 150 },
        { key: "description", title: "Описание", width: 240 },
        { key: "actions", title: "Действия", width: 140 }
    ]);
    createSearchableRadioSelect(status);
    search.addEventListener("input", render);
    status.addEventListener("change", render);
    editForm.addEventListener("submit", event => void save(event, false));
    createForm.addEventListener("submit", event => void save(event, true));
    page.addEventListener("click", event => {
        const target = event.target;
        if (!(target instanceof Element)) return;
        const button = target.closest<HTMLElement>("[data-action]");
        if (button?.dataset.action === "new-container") void runCommand(openCreate);
        else if (button?.dataset.action === "edit-container") void runCommand(() => openEdit(button.dataset.containerId!));
        else if (button?.dataset.action === "delete-container") void runCommand(deleteContainer);
    });
    for (const modal of [editModal, createModal]) {
        modal.addEventListener("hide.bs.modal", event => {
            if (state.saving) event.preventDefault();
        });
        modal.addEventListener("hidden.bs.modal", () => {
            editServerSelect.close();
            createServerSelect.close();
            const trigger = Array.from(list.querySelectorAll<HTMLButtonElement>("[data-container-id]"))
                .find(button => button.dataset.containerId === state.selectedId);
            (trigger ?? search).focus();
        });
        modal.addEventListener("shown.bs.modal", () => {
            const input = modal.querySelector<HTMLInputElement>('input[type="url"]');
            if (!input) throw new Error("Поле адреса контейнера не найдено.");
            input.focus();
        });
    }
    await runCommand(load);
}

async function load(): Promise<void> {
    const [serversResponse, containersResponse] = await Promise.all([
        sendJsonRequest<ServiceResultWithData<Server[]>>("/api/admin/objects/servers"),
        sendJsonRequest<ServiceResultWithData<Container[]>>("/api/admin/objects/containers")
    ]);
    state.servers = unwrapServiceResult(serversResponse);
    state.containers = unwrapServiceResult(containersResponse);
    render();
}

function render(): void {
    const containers = state.containers.filter(container =>
        matchesSearch(search.value, container.lastKnownName, container.endpointUrl, container.description, serverName(container))
        && (status.value === "" || String(container.status) === status.value));
    table.body.replaceChildren(...containers.map(container => {
        const button = createTableAction("Изменить");
        button.dataset.action = "edit-container";
        button.dataset.containerId = container.id;
        return table.row([
            container.lastKnownName ?? "Контейнер", serverName(container), container.endpointUrl, statusBadge(container.status),
            container.isMonitoringEnabled ? "Включён" : "Отключён", String(container.consecutiveFailures),
            container.description ?? "—", button
        ]);
    }));
    empty.classList.toggle("d-none", containers.length !== 0);
    empty.textContent = state.containers.length === 0 ? "Контейнеров пока нет." : "По выбранным фильтрам контейнеры не найдены.";
    table.refresh();
}

function serverName(container: Container): string {
    if (container.serverId === null) return "Без сервера";
    const server = state.servers.find(item => item.id === container.serverId);
    if (!server) throw new Error("Сервер контейнера не найден.");
    return server.name;
}

function fillServers(select: HTMLSelectElement, serverId: string | null): void {
    select.replaceChildren();
    const none = document.createElement("option");
    none.value = "";
    none.textContent = "Без сервера";
    select.append(none);
    for (const server of state.servers) {
        const option = document.createElement("option");
        option.value = server.id;
        option.textContent = server.name;
        select.append(option);
    }
    select.value = serverId ?? "";
    select.disabled = state.servers.length === 0;
}

async function openCreate(): Promise<void> {
    await load();
    createForm.reset();
    clearErrors(createForm);
    fillServers(createServer, null);
    createServerSelect.refresh();
    modal(createModal).show();
}

async function openEdit(id: string): Promise<void> {
    await load();
    const container = requireContainer(id);
    state.selectedId = id;
    clearErrors(editForm);
    requireElement<HTMLElement>("[data-container-form-title]").textContent = container.lastKnownName ?? "Контейнер";
    field<HTMLInputElement>(editForm, "endpointUrl").value = container.endpointUrl;
    field<HTMLTextAreaElement>(editForm, "description").value = container.description ?? "";
    field<HTMLInputElement>(editForm, "isMonitoringEnabled").checked = container.isMonitoringEnabled;
    fillServers(editServer, container.serverId);
    editServerSelect.refresh();
    requireElement<HTMLElement>("[data-container-danger]").classList.remove("d-none");
    modal(editModal).show();
}

async function save(event: SubmitEvent, creating: boolean): Promise<void> {
    event.preventDefault();
    if (state.saving) return;
    const form = creating ? createForm : editForm;
    clearErrors(form);
    const container = creating ? null : requireContainer(state.selectedId);
    const select = creating ? createServer : editServer;
    const payload = {
        endpointUrl: field<HTMLInputElement>(form, "endpointUrl").value,
        description: field<HTMLTextAreaElement>(form, "description").value || null,
        isMonitoringEnabled: field<HTMLInputElement>(form, "isMonitoringEnabled").checked,
        serverId: select.value || null,
        version: container?.version
    };
    setSaving(true);
    try {
        const response = await sendJsonRequest<ServiceResultWithData<Container>>(
            creating ? "/api/admin/objects/containers" : `/api/admin/objects/containers/${container!.id}`,
            creating ? "POST" : "PUT", payload);
        unwrapServiceResult(response);
        await load();
        setSaving(false);
        modal(creating ? createModal : editModal).hide();
    }
    catch (error: unknown) {
        showFormError(form, error);
    }
    finally {
        setSaving(false);
    }
}

async function deleteContainer(): Promise<void> {
    if (state.saving) return;
    const container = requireContainer(state.selectedId);
    if (!window.confirm("Удалить выбранный контейнер?")) return;
    setSaving(true);
    try {
        const response = await sendJsonRequest<ServiceResult>(`/api/admin/objects/containers/${container.id}`, "DELETE", { version: container.version });
        unwrapServiceSuccess(response);
        await load();
        setSaving(false);
        modal(editModal).hide();
    }
    finally {
        setSaving(false);
    }
}

function setSaving(saving: boolean): void {
    state.saving = saving;
    if (saving) {
        for (const control of page.querySelectorAll<HTMLButtonElement | HTMLInputElement | HTMLSelectElement | HTMLTextAreaElement>("button, input, select, textarea")) {
            if (!control.disabled) temporarilyDisabled.add(control);
            control.disabled = true;
        }
    }
    else {
        for (const control of temporarilyDisabled) control.disabled = false;
        temporarilyDisabled.clear();
    }
    editServerSelect.refresh();
    createServerSelect.refresh();
}

async function runCommand(command: () => Promise<void>): Promise<void> {
    pageError.classList.add("d-none");
    try { await command(); }
    catch (error: unknown) {
        if (editModal.classList.contains("show")) showFormError(editForm, error);
        else {
            pageError.textContent = error instanceof Error ? error.message : "Не удалось выполнить действие.";
            pageError.classList.remove("d-none");
        }
    }
}

function showFormError(form: HTMLFormElement, error: unknown): void {
    if (error instanceof ApiError && error.field) {
        const control = form.querySelector<HTMLElement>(`[name="${error.field}"]`);
        const feedback = form.querySelector<HTMLElement>(`[data-container-error="${error.field}"], [data-create-container-error="${error.field}"]`);
        if (control && feedback) {
            control.classList.add("is-invalid");
            feedback.textContent = error.message;
            feedback.classList.add("d-block");
            return;
        }
    }
    const target = form.querySelector<HTMLElement>("[role='alert']");
    if (!target) throw new Error("Контейнер ошибки не найден.");
    target.textContent = error instanceof Error ? error.message : "Не удалось сохранить контейнер.";
    target.classList.remove("d-none");
}

function clearErrors(form: HTMLFormElement): void {
    for (const control of form.querySelectorAll<HTMLElement>(".is-invalid")) control.classList.remove("is-invalid");
    for (const feedback of form.querySelectorAll<HTMLElement>(".invalid-feedback")) {
        feedback.textContent = "";
        feedback.classList.remove("d-block");
    }
    const alert = form.querySelector<HTMLElement>("[role='alert']");
    if (!alert) throw new Error("Контейнер ошибки не найден.");
    alert.classList.add("d-none");
}

function field<T extends Element>(form: HTMLFormElement, name: string): T {
    const result = form.querySelector<T>(`[name="${name}"]`);
    if (!result) throw new Error(`Поле ${name} не найдено.`);
    return result;
}

function requireContainer(id: string | null): Container {
    const container = state.containers.find(item => item.id === id);
    if (!container) throw new Error("Контейнер не найден.");
    return container;
}

function requireElement<T extends Element>(selector: string): T {
    const element = document.querySelector<T>(selector);
    if (!element) throw new Error(`Обязательный элемент не найден: ${selector}.`);
    return element;
}

function modal(element: Element): { show(): void; hide(): void; } {
    const bootstrap = (window as BootstrapWindow).bootstrap;
    if (!bootstrap) throw new Error("Bootstrap не загружен.");
    return bootstrap.Modal.getOrCreateInstance(element);
}

function statusBadge(status: number): HTMLElement {
    const badge = document.createElement("span");
    badge.classList.add("badge", "rounded-pill");
    if (status === 0) { badge.textContent = "Неизвестно"; badge.classList.add("text-bg-secondary"); }
    else if (status === 1) { badge.textContent = "Доступен"; badge.classList.add("text-bg-success"); }
    else if (status === 2) { badge.textContent = "Недоступен"; badge.classList.add("text-bg-danger"); }
    else throw new Error("Неизвестный статус мониторинга.");
    return badge;
}
