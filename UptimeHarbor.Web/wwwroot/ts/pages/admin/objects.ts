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
    lastKnownName: string | null;
    status: MonitoringStatus;
    consecutiveFailures: number;
}

interface AdminContainer extends ObjectSettings {
    id: string;
    serverId: string;
    lastKnownName: string | null;
    status: MonitoringStatus;
    consecutiveFailures: number;
}

interface ServerDeletionImpact {
    serverId: string;
    containerCount: number;
}

interface PageState {
    servers: AdminServer[];
    containers: AdminContainer[];
    selectedServerId: string | null;
    selectedContainerId: string | null;
    creatingServer: boolean;
    creatingContainer: boolean;
}

const state: PageState = {
    servers: [],
    containers: [],
    selectedServerId: null,
    selectedContainerId: null,
    creatingServer: false,
    creatingContainer: false
};

const page = requireElement<HTMLElement>("[data-objects-page]");
const pageError = requireElement<HTMLElement>("[data-page-error]");
const serverList = requireElement<HTMLElement>("[data-server-list]");
const serverEmpty = requireElement<HTMLElement>("[data-server-empty]");
const selectionPlaceholder = requireElement<HTMLElement>("[data-selection-placeholder]");
const serverForm = requireElement<HTMLFormElement>("[data-server-form]");
const serverFormTitle = requireElement<HTMLElement>("[data-server-form-title]");
const serverEndpoint = requireElement<HTMLInputElement>("[data-server-endpoint]");
const serverDescription = requireElement<HTMLTextAreaElement>("[data-server-description]");
const serverEnabled = requireElement<HTMLInputElement>("[data-server-enabled]");
const serverDanger = requireElement<HTMLElement>("[data-server-danger]");
const containersSection = requireElement<HTMLElement>("[data-containers-section]");
const containerList = requireElement<HTMLElement>("[data-container-list]");
const containerEmpty = requireElement<HTMLElement>("[data-container-empty]");
const containerForm = requireElement<HTMLFormElement>("[data-container-form]");
const containerFormTitle = requireElement<HTMLElement>("[data-container-form-title]");
const containerEndpoint = requireElement<HTMLInputElement>("[data-container-endpoint]");
const containerDescription = requireElement<HTMLTextAreaElement>("[data-container-description]");
const containerEnabled = requireElement<HTMLInputElement>("[data-container-enabled]");
const containerDanger = requireElement<HTMLElement>("[data-container-danger]");

document.addEventListener("DOMContentLoaded", () => {
    void initObjectsPage();
});

async function initObjectsPage(): Promise<void> {
    page.addEventListener("click", handlePageClick);
    serverList.addEventListener("click", handleServerSelection);
    containerList.addEventListener("click", handleContainerSelection);
    serverForm.addEventListener("submit", event => void saveServer(event));
    containerForm.addEventListener("submit", event => void saveContainer(event));

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
    state.selectedContainerId = null;
    state.creatingServer = false;
    state.creatingContainer = false;
    fillServerForm(server);
    renderServers();

    const response = await sendJsonRequest<ServiceResultWithData<AdminContainer[]>>(
        `/api/admin/objects/servers/${serverId}/containers`);
    state.containers = unwrapServiceResult(response);
    renderContainers();
}

function startCreatingServer(): void {
    clearPageError();
    state.selectedServerId = null;
    state.selectedContainerId = null;
    state.containers = [];
    state.creatingServer = true;
    state.creatingContainer = false;
    renderServers();
    selectionPlaceholder.classList.add("d-none");
    serverForm.classList.remove("d-none");
    serverDanger.classList.add("d-none");
    containersSection.classList.add("d-none");
    serverFormTitle.textContent = "Новый сервер";
    serverForm.reset();
    clearFormErrors(serverForm);
    serverEndpoint.focus();
}

function startCreatingContainer(): void {
    if (state.selectedServerId === null) {
        throw new Error("Сначала выберите существующий сервер.");
    }

    clearPageError();
    state.selectedContainerId = null;
    state.creatingContainer = true;
    renderContainers();
    containerForm.classList.remove("d-none");
    containerDanger.classList.add("d-none");
    containerFormTitle.textContent = "Новый контейнер";
    containerForm.reset();
    clearFormErrors(containerForm);
    containerEndpoint.focus();
}

async function saveServer(event: SubmitEvent): Promise<void> {
    event.preventDefault();
    clearPageError();
    clearFormErrors(serverForm);
    const settings = readSettings(serverEndpoint, serverDescription, serverEnabled);

    try {
        const url = state.creatingServer
            ? "/api/admin/objects/servers"
            : `/api/admin/objects/servers/${requireSelectedServerId()}`;
        const method = state.creatingServer ? "POST" : "PUT";
        const response = await sendJsonRequest<ServiceResultWithData<AdminServer>>(url, method, settings);
        const savedServer = unwrapServiceResult(response);
        await loadServers(savedServer.id);
    }
    catch (error: unknown) {
        showFormOrPageError(error, serverForm);
    }
}

async function saveContainer(event: SubmitEvent): Promise<void> {
    event.preventDefault();
    clearPageError();
    clearFormErrors(containerForm);
    const settings = readSettings(containerEndpoint, containerDescription, containerEnabled);

    try {
        const serverId = requireSelectedServerId();
        const url = state.creatingContainer
            ? `/api/admin/objects/servers/${serverId}/containers`
            : `/api/admin/objects/containers/${requireSelectedContainerId()}`;
        const method = state.creatingContainer ? "POST" : "PUT";
        const response = await sendJsonRequest<ServiceResultWithData<AdminContainer>>(url, method, settings);
        const savedContainer = unwrapServiceResult(response);
        await selectServer(serverId);
        selectContainer(savedContainer.id);
    }
    catch (error: unknown) {
        showFormOrPageError(error, containerForm);
    }
}

async function deleteServer(): Promise<void> {
    const serverId = requireSelectedServerId();
    const impactResponse = await sendJsonRequest<ServiceResultWithData<ServerDeletionImpact>>(
        `/api/admin/objects/servers/${serverId}/deletion-impact`);
    const impact = unwrapServiceResult(impactResponse);
    const confirmed = window.confirm(
        `Удалить сервер и ${impact.containerCount} контейнер(ов)? Права и привязки уведомлений также будут удалены.`);
    if (!confirmed) {
        return;
    }

    const response = await sendJsonRequest<ServiceResult>(
        `/api/admin/objects/servers/${serverId}`,
        "DELETE",
        { expectedContainerCount: impact.containerCount });
    unwrapServiceSuccess(response);
    clearSelection();
    await loadServers();
}

async function deleteContainer(): Promise<void> {
    const containerId = requireSelectedContainerId();
    if (!window.confirm("Удалить выбранный контейнер?")) {
        return;
    }

    const response = await sendJsonRequest<ServiceResult>(
        `/api/admin/objects/containers/${containerId}`,
        "DELETE");
    unwrapServiceSuccess(response);
    await selectServer(requireSelectedServerId());
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

    if (action === "create-server") {
        startCreatingServer();
    }
    else if (action === "create-container") {
        startCreatingContainer();
    }
    else if (action === "delete-server") {
        void runPageCommand(deleteServer);
    }
    else if (action === "delete-container") {
        void runPageCommand(deleteContainer);
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
        void runPageCommand(() => selectServer(button.dataset.serverId!));
    }
}

function handleContainerSelection(event: MouseEvent): void {
    const target = event.target;
    if (!(target instanceof Element)) {
        return;
    }

    const button = target.closest<HTMLButtonElement>("button[data-container-id]");
    if (button?.dataset.containerId) {
        selectContainer(button.dataset.containerId);
    }
}

function selectContainer(containerId: string): void {
    const container = state.containers.find(item => item.id === containerId);
    if (!container) {
        throw new Error("Выбранный контейнер не найден в состоянии страницы.");
    }

    state.selectedContainerId = containerId;
    state.creatingContainer = false;
    fillContainerForm(container);
    renderContainers();
}

function renderServers(): void {
    serverList.replaceChildren();
    serverEmpty.classList.toggle("d-none", state.servers.length !== 0);

    for (const server of state.servers) {
        serverList.append(createObjectButton(
            server,
            server.id === state.selectedServerId,
            "serverId"));
    }
}

function renderContainers(): void {
    containerList.replaceChildren();
    containerEmpty.classList.toggle("d-none", state.containers.length !== 0);
    containersSection.classList.remove("d-none");

    for (const container of state.containers) {
        containerList.append(createObjectButton(
            container,
            container.id === state.selectedContainerId,
            "containerId"));
    }

    if (state.selectedContainerId === null && !state.creatingContainer) {
        containerForm.classList.add("d-none");
    }
}

function createObjectButton(
    item: AdminServer | AdminContainer,
    selected: boolean,
    idDataName: "serverId" | "containerId"
): HTMLButtonElement {
    const button = document.createElement("button");
    button.type = "button";
    button.classList.add("list-group-item", "list-group-item-action");
    button.classList.toggle("active", selected);
    button.dataset[idDataName] = item.id;

    const title = document.createElement("span");
    title.classList.add("d-block", "fw-semibold");
    title.textContent = item.lastKnownName ?? "Имя не получено";

    const details = document.createElement("span");
    details.classList.add("d-block", "small");
    details.textContent = `${shortGuid(item.id)} · ${statusText(item.status)} · ${item.endpointUrl}`;

    button.append(title, details);
    return button;
}

function fillServerForm(server: AdminServer): void {
    selectionPlaceholder.classList.add("d-none");
    serverForm.classList.remove("d-none");
    serverDanger.classList.remove("d-none");
    containersSection.classList.remove("d-none");
    serverFormTitle.textContent = server.lastKnownName ?? `Имя не получено · ${shortGuid(server.id)}`;
    serverEndpoint.value = server.endpointUrl;
    serverDescription.value = server.description ?? "";
    serverEnabled.checked = server.isMonitoringEnabled;
    clearFormErrors(serverForm);
}

function fillContainerForm(container: AdminContainer): void {
    containerForm.classList.remove("d-none");
    containerDanger.classList.remove("d-none");
    containerFormTitle.textContent = container.lastKnownName ?? `Имя не получено · ${shortGuid(container.id)}`;
    containerEndpoint.value = container.endpointUrl;
    containerDescription.value = container.description ?? "";
    containerEnabled.checked = container.isMonitoringEnabled;
    clearFormErrors(containerForm);
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

function showFormOrPageError(error: unknown, form: HTMLFormElement): void {
    if (error instanceof ApiError && error.field) {
        const field = form.querySelector<HTMLInputElement | HTMLTextAreaElement>(`[name="${error.field}"]`);
        const message = form.querySelector<HTMLElement>(`[data-server-error="${error.field}"], [data-container-error="${error.field}"]`);
        if (field && message) {
            field.classList.add("is-invalid");
            message.textContent = error.message;
            return;
        }
    }

    showPageError(error);
}

function clearFormErrors(form: HTMLFormElement): void {
    for (const field of form.querySelectorAll<HTMLElement>(".is-invalid")) {
        field.classList.remove("is-invalid");
    }
    for (const message of form.querySelectorAll<HTMLElement>(".invalid-feedback")) {
        message.textContent = "";
    }
}

function clearSelection(): void {
    state.selectedServerId = null;
    state.selectedContainerId = null;
    state.creatingServer = false;
    state.creatingContainer = false;
    state.containers = [];
    serverForm.classList.add("d-none");
    containersSection.classList.add("d-none");
    selectionPlaceholder.classList.remove("d-none");
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

function requireSelectedContainerId(): string {
    if (state.selectedContainerId === null) {
        throw new Error("Контейнер не выбран.");
    }
    return state.selectedContainerId;
}

function requireElement<TElement extends Element>(selector: string): TElement {
    const element = document.querySelector<TElement>(selector);
    if (!element) {
        throw new Error(`Обязательный элемент не найден: ${selector}.`);
    }
    return element;
}

function shortGuid(id: string): string {
    return id.slice(0, 8);
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
    pageError.textContent = error instanceof Error ? error.message : "Неизвестная ошибка.";
    pageError.classList.remove("d-none");
}

function clearPageError(): void {
    pageError.textContent = "";
    pageError.classList.add("d-none");
}
