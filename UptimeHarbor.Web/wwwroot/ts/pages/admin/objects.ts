import { createSearchableMultiSelect } from "../../shared/searchableMultiSelect.js";
import { createSearchableRadioSelect } from "../../shared/searchableRadioSelect.js";
import { createDataTable, createTableAction, matchesSearch, TableColumn } from "../../shared/dataTable.js";
import {
    ApiError,
    sendJsonRequest,
    ServiceResult,
    ServiceResultWithData,
    unwrapServiceResult,
    unwrapServiceSuccess
} from "../../shared/http.js";

enum MonitoringStatus {
    Unknown = 0,
    Available = 1,
    Unavailable = 2
}

interface ObjectSettings {
    endpointUrl: string;
    description: string | null;
    isMonitoringEnabled: boolean;
}

interface AdminServer extends ObjectSettings {
    id: string;
    name: string;
    lastKnownName: string | null;
    status: MonitoringStatus;
    consecutiveFailures: number;
    version: number;
}

interface AdminContainer extends ObjectSettings {
    id: string;
    serverId: string | null;
    lastKnownName: string | null;
    status: MonitoringStatus;
    consecutiveFailures: number;
    version: number;
}

interface ServerDeletionImpact {
    serverId: string;
    containerCount: number;
    version: number;
}

interface BootstrapModalInstance {
    show(): void;
    hide(): void;
}

interface BootstrapWindow extends Window {
    bootstrap?: { Modal: { getOrCreateInstance(element: Element): BootstrapModalInstance } };
}

interface PageState {
    servers: AdminServer[];
    containers: AdminContainer[];
    selectedServerId: string | null;
}

const state: PageState = {
    servers: [],
    containers: [],
    selectedServerId: null
};

const page = requireElement<HTMLElement>("[data-objects-page]");
const pageError = requireElement<HTMLElement>("[data-page-error]");
const serverList = requireElement<HTMLElement>("[data-server-list]");
const serverEmpty = requireElement<HTMLElement>("[data-server-empty]");
const editObjectModal = requireElement<HTMLElement>("[data-edit-object-modal]");
const editObjectError = requireElement<HTMLElement>("[data-edit-object-error]");
const serverForm = requireElement<HTMLFormElement>("[data-server-form]");
const serverFormTitle = requireElement<HTMLElement>("[data-server-form-title]");
const serverName = requireElement<HTMLInputElement>("[data-server-name]");
const serverEndpoint = requireElement<HTMLInputElement>("[data-server-endpoint]");
const serverDescription = requireElement<HTMLTextAreaElement>("[data-server-description]");
const serverEnabled = requireElement<HTMLInputElement>("[data-server-enabled]");
const serverDanger = requireElement<HTMLElement>("[data-server-danger]");
const createServerModal = requireElement<HTMLElement>("[data-create-server-modal]");
const createServerForm = requireElement<HTMLFormElement>("[data-create-server-form]");
const createServerName = requireElement<HTMLInputElement>("[data-create-server-name]");
const createServerEndpoint = requireElement<HTMLInputElement>("[data-create-server-endpoint]");
const createServerDescription = requireElement<HTMLTextAreaElement>("[data-create-server-description]");
const createServerEnabled = requireElement<HTMLInputElement>("[data-create-server-enabled]");
const createServerGeneralError = requireElement<HTMLElement>("[data-create-server-general-error]");
const serverSearch = requireElement<HTMLInputElement>("[data-server-search]");
const serverStatusFilter = requireElement<HTMLSelectElement>("[data-server-status-filter]");
const serverContainers = requireElement<HTMLSelectElement>("[data-server-containers]");
const containerSelect = createSearchableMultiSelect(serverContainers, { buttonPlaceholder: "Не выбрано", noOptionsText: "Нет объектов." });
let serversTable: ReturnType<typeof createDataTable>;

document.addEventListener("DOMContentLoaded", () => {
    void initObjectsPage();
});

async function initObjectsPage(): Promise<void> {
    const settingsColumns: TableColumn[] = [
        { key: "endpoint", title: "Адрес проверки", width: 280, help: "HTTP endpoint, который опрашивает мониторинг. Этот адрес виден только администраторам." },
        { key: "status", title: "Статус", width: 150, help: "Последний результат мониторинга. «Неизвестно» — результат ещё не получен." },
        { key: "enabled", title: "Мониторинг", width: 160, help: "Показывает, включены ли автоматические проверки объекта." },
        { key: "failures", title: "Сбои подряд", width: 160, help: "Число последовательных неуспешных проверок объекта." },
        { key: "description", title: "Описание", width: 240 },
        { key: "actions", title: "Действия", width: 140 }
    ];
    serversTable = createDataTable(serverList, "servers", "Серверы", [
        { key: "name", title: "Сервер", width: 220 },
        { key: "internalName", title: "Внутреннее имя", width: 200, help: "Имя, полученное от сервера при проверке; оно не заменяет заданное администратором имя." },
        ...settingsColumns
    ]);
    serverSearch.addEventListener("input", renderServers);
    createSearchableRadioSelect(serverStatusFilter);
    serverStatusFilter.addEventListener("change", renderServers);
    page.addEventListener("click", handlePageClick);
    serverList.addEventListener("click", handleServerSelection);
    serverForm.addEventListener("submit", event => void saveServer(event));
    createServerForm.addEventListener("submit", event => void createServer(event));
    createServerModal.addEventListener("show.bs.modal", resetCreateServerForm);
    createServerModal.addEventListener("shown.bs.modal", () => createServerName.focus());
    editObjectModal.addEventListener("shown.bs.modal", () => {
        serverName.focus();
    });
    editObjectModal.addEventListener("hidden.bs.modal", () => {
        containerSelect.close();
        const trigger = Array.from(serverList.querySelectorAll<HTMLButtonElement>("button[data-server-id]"))
            .find(button => button.dataset.serverId === state.selectedServerId);
        (trigger ?? serverSearch).focus();
    });

    try {
        await loadServers();
    }
    catch (error: unknown) {
        showPageError(error);
    }
}

async function loadServers(preferredServerId: string | null = null): Promise<void> {
    const response = await sendJsonRequest<ServiceResultWithData<AdminServer[]>>(
        "/api/admin/objects/servers");
    state.servers = unwrapServiceResult(response);
    renderServers();

    const serverId = preferredServerId ?? state.selectedServerId;
    if (serverId && state.servers.some(server => server.id === serverId)) {
        await selectServer(serverId);
    }
    else if (state.selectedServerId !== null) {
        clearSelection();
    }
}

async function selectServer(serverId: string): Promise<void> {
    const server = requireServer(serverId);
    state.selectedServerId = serverId;
    fillServerForm(server);
    renderServers();

    const response = await sendJsonRequest<ServiceResultWithData<AdminContainer[]>>(
        "/api/admin/objects/containers");
    state.containers = unwrapServiceResult(response);
    renderContainerLinks();
}

async function saveServer(event: SubmitEvent): Promise<void> {
    event.preventDefault();
    clearPageError();
    clearFormErrors(serverForm);
    const settings = {
        name: serverName.value,
        ...readSettings(serverEndpoint, serverDescription, serverEnabled)
    };

    try {
        const serverId = requireSelectedServerId();
        const selectedContainerIds = new Set(Array.from(serverContainers.selectedOptions, option => option.value));
        const payload = {
            ...settings,
            version: requireServer(serverId).version,
            containerLinks: state.containers
                .filter(container => container.serverId === serverId || selectedContainerIds.has(container.id))
                .map(container => ({
                    id: container.id,
                    version: container.version,
                    isLinked: selectedContainerIds.has(container.id)
                }))
        };
        const response = await sendJsonRequest<ServiceResultWithData<AdminServer>>(
            `/api/admin/objects/servers/${serverId}`,
            "PUT",
            payload);
        const savedServer = unwrapServiceResult(response);
        await loadServers(savedServer.id);
        hideModal(editObjectModal);
    }
    catch (error: unknown) {
        showFormOrPageError(error, serverForm, "data-server-error");
    }
}

async function createServer(event: SubmitEvent): Promise<void> {
    event.preventDefault();
    clearFormErrors(createServerForm);
    hideModalError(createServerGeneralError);

    try {
        const response = await sendJsonRequest<ServiceResultWithData<AdminServer>>(
            "/api/admin/objects/servers",
            "POST",
            {
                name: createServerName.value,
                ...readSettings(createServerEndpoint, createServerDescription, createServerEnabled)
            });
        const savedServer = unwrapServiceResult(response);
        hideModal(createServerModal);
        await loadServers(savedServer.id);
    }
    catch (error: unknown) {
        if (!showFormFieldError(error, createServerForm, "data-create-server-error")) {
            showModalError(createServerGeneralError, error, "Не удалось создать сервер.");
        }
    }
}

async function deleteServer(): Promise<void> {
    const serverId = requireSelectedServerId();
    const impactResponse = await sendJsonRequest<ServiceResultWithData<ServerDeletionImpact>>(
        `/api/admin/objects/servers/${serverId}/deletion-impact`);
    const impact = unwrapServiceResult(impactResponse);
    const confirmed = window.confirm(
        `Удалить сервер? ${impact.containerCount} контейнер(ов) сохранятся без связи с сервером. Права и привязки уведомлений самого сервера будут удалены.`);
    if (!confirmed) {
        return;
    }

    const response = await sendJsonRequest<ServiceResult>(
        `/api/admin/objects/servers/${serverId}`,
        "DELETE",
        { expectedContainerCount: impact.containerCount, version: impact.version });
    unwrapServiceSuccess(response);
    clearSelection();
    await loadServers();
}

function handlePageClick(event: MouseEvent): void {
    const target = event.target;
    if (!(target instanceof Element)) {
        return;
    }

    const actionElement = target.closest<HTMLElement>("[data-action]");
    const action = actionElement?.dataset.action;
    if (!action) {
        return;
    }

    if (action === "delete-server") {
        void runPageCommand(deleteServer);
    }
    else {
        throw new Error(`Неизвестное действие: ${action}.`);
    }
}

function handleServerSelection(event: MouseEvent): void {
    const target = event.target;
    if (!(target instanceof Element)) {
        return;
    }

    const button = target.closest<HTMLButtonElement>("button[data-server-id]");
    if (button?.dataset.serverId) {
        void runPageCommand(async () => {
            await loadServers(button.dataset.serverId!);
            showModal(editObjectModal);
        });
    }
}

function renderServers(): void {
    const servers = state.servers.filter(server =>
        matchesSearch(serverSearch.value, server.name, server.lastKnownName, server.endpointUrl, server.description)
        && (serverStatusFilter.value === "" || String(server.status) === serverStatusFilter.value));
    serversTable.body.replaceChildren(...servers.map(server => createObjectRow(server, "serverId")));
    serverEmpty.classList.toggle("d-none", servers.length !== 0);
    serverEmpty.textContent = state.servers.length === 0 ? "Серверов пока нет." : "По выбранным фильтрам серверы не найдены.";
    serversTable.refresh();
}

function renderContainerLinks(): void {
    serverContainers.replaceChildren();
    for (const container of state.containers) {
        if (container.serverId !== null && container.serverId !== state.selectedServerId) continue;
        const option = document.createElement("option");
        option.value = container.id;
        option.textContent = container.lastKnownName ?? container.endpointUrl;
        option.selected = container.serverId === state.selectedServerId;
        serverContainers.append(option);
    }
    serverContainers.disabled = serverContainers.options.length === 0;
    containerSelect.refresh();
}

function createObjectRow(item: AdminServer, idDataName: "serverId"): HTMLTableRowElement {
    const button = createTableAction("Изменить");
    button.dataset[idDataName] = item.id;
    return serversTable.row([
        item.name, item.lastKnownName ?? "Нет данных", item.endpointUrl, createStatusBadge(item.status),
        item.isMonitoringEnabled ? "Включён" : "Отключён", String(item.consecutiveFailures),
        item.description ?? "—", button
    ], item.id === state.selectedServerId);
}

function fillServerForm(server: AdminServer): void {
    serverForm.classList.remove("d-none");
    serverDanger.classList.remove("d-none");
    serverFormTitle.textContent = server.name;
    serverName.value = server.name;
    serverEndpoint.value = server.endpointUrl;
    serverDescription.value = server.description ?? "";
    serverEnabled.checked = server.isMonitoringEnabled;
    clearFormErrors(serverForm);
}

function readSettings(
    endpoint: HTMLInputElement,
    description: HTMLTextAreaElement,
    enabled: HTMLInputElement
): ObjectSettings {
    return {
        endpointUrl: endpoint.value,
        description: description.value || null,
        isMonitoringEnabled: enabled.checked
    };
}

function showFormOrPageError(error: unknown, form: HTMLFormElement, errorAttribute: string): void {
    if (!showFormFieldError(error, form, errorAttribute)) {
        showPageError(error);
    }
}

function showFormFieldError(error: unknown, form: HTMLFormElement, errorAttribute: string): boolean {
    if (!(error instanceof ApiError) || !error.field) {
        return false;
    }

    const field = form.querySelector<HTMLInputElement | HTMLTextAreaElement | HTMLSelectElement>(`[name="${error.field}"]`);
    const message = form.querySelector<HTMLElement>(`[${errorAttribute}="${error.field}"]`);
    if (!field || !message) {
        return false;
    }

    field.classList.add("is-invalid");
    message.textContent = error.message;
    message.classList.add("d-block");
    return true;
}

function clearFormErrors(form: HTMLFormElement): void {
    for (const field of form.querySelectorAll<HTMLElement>(".is-invalid")) {
        field.classList.remove("is-invalid");
    }
    for (const message of form.querySelectorAll<HTMLElement>(".invalid-feedback")) {
        message.textContent = "";
        message.classList.remove("d-block");
    }
}

function clearSelection(): void {
    state.selectedServerId = null;
    state.containers = [];
    serverForm.classList.add("d-none");
    hideModal(editObjectModal);
    renderServers();
}

function requireServer(serverId: string): AdminServer {
    const server = state.servers.find(item => item.id === serverId);
    if (!server) {
        throw new Error("Выбранный сервер не найден в состоянии страницы.");
    }
    return server;
}

function requireSelectedServerId(): string {
    if (state.selectedServerId === null) {
        throw new Error("Сервер не выбран.");
    }
    return state.selectedServerId;
}

function requireElement<TElement extends Element>(selector: string): TElement {
    const element = document.querySelector<TElement>(selector);
    if (!element) {
        throw new Error(`Обязательный элемент не найден: ${selector}.`);
    }
    return element;
}

function statusText(status: MonitoringStatus): string {
    if (status === MonitoringStatus.Unknown) {
        return "Неизвестно";
    }
    if (status === MonitoringStatus.Available) {
        return "Доступен";
    }
    if (status === MonitoringStatus.Unavailable) {
        return "Недоступен";
    }
    throw new Error(`Неизвестный статус: ${String(status)}.`);
}

function createStatusBadge(status: MonitoringStatus): HTMLElement {
    const badge = document.createElement("span");
    badge.classList.add("badge", "rounded-pill");
    badge.textContent = statusText(status);

    if (status === MonitoringStatus.Available) {
        badge.classList.add("text-bg-success");
    }
    else if (status === MonitoringStatus.Unavailable) {
        badge.classList.add("text-bg-danger");
    }
    else if (status === MonitoringStatus.Unknown) {
        badge.classList.add("text-bg-secondary");
    }
    else {
        throw new Error(`Неизвестный статус: ${String(status)}.`);
    }

    return badge;
}

function resetCreateServerForm(): void {
    clearPageError();
    createServerForm.reset();
    clearFormErrors(createServerForm);
    hideModalError(createServerGeneralError);
}

function showModalError(element: HTMLElement, error: unknown, fallbackMessage: string): void {
    element.textContent = error instanceof Error ? error.message : fallbackMessage;
    element.classList.remove("d-none");
}

function hideModalError(element: HTMLElement): void {
    element.textContent = "";
    element.classList.add("d-none");
}

function hideModal(element: Element): void {
    const bootstrapApi = (window as BootstrapWindow).bootstrap;
    if (!bootstrapApi) {
        throw new Error("Bootstrap не загружен.");
    }

    bootstrapApi.Modal.getOrCreateInstance(element).hide();
}

function showModal(element: Element): void {
    const bootstrapApi = (window as BootstrapWindow).bootstrap;
    if (!bootstrapApi) throw new Error("Bootstrap не загружен.");
    bootstrapApi.Modal.getOrCreateInstance(element).show();
}

async function runPageCommand(command: () => Promise<void>): Promise<void> {
    clearPageError();
    try {
        await command();
    }
    catch (error: unknown) {
        showPageError(error);
    }
}

function showPageError(error: unknown): void {
    const target = editObjectModal.classList.contains("show") ? editObjectError : pageError;
    target.textContent = error instanceof Error ? error.message : "Неизвестная ошибка.";
    target.classList.remove("d-none");
}

function clearPageError(): void {
    pageError.textContent = "";
    pageError.classList.add("d-none");
    hideModalError(editObjectError);
}
