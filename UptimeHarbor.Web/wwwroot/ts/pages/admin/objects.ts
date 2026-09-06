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
    serverId: string;
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
    hide(): void;
}

interface BootstrapWindow extends Window {
    bootstrap?: { Modal: { getOrCreateInstance(element: Element): BootstrapModalInstance } };
}

interface PageState {
    servers: AdminServer[];
    containers: AdminContainer[];
    selectedServerId: string | null;
    selectedContainerId: string | null;
}

const state: PageState = {
    servers: [],
    containers: [],
    selectedServerId: null,
    selectedContainerId: null
};

const page = requireElement<HTMLElement>("[data-objects-page]");
const pageError = requireElement<HTMLElement>("[data-page-error]");
const serverList = requireElement<HTMLElement>("[data-server-list]");
const serverEmpty = requireElement<HTMLElement>("[data-server-empty]");
const selectionPlaceholder = requireElement<HTMLElement>("[data-selection-placeholder]");
const serverForm = requireElement<HTMLFormElement>("[data-server-form]");
const serverFormTitle = requireElement<HTMLElement>("[data-server-form-title]");
const serverName = requireElement<HTMLInputElement>("[data-server-name]");
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
const createServerModal = requireElement<HTMLElement>("[data-create-server-modal]");
const createServerForm = requireElement<HTMLFormElement>("[data-create-server-form]");
const createServerName = requireElement<HTMLInputElement>("[data-create-server-name]");
const createServerEndpoint = requireElement<HTMLInputElement>("[data-create-server-endpoint]");
const createServerDescription = requireElement<HTMLTextAreaElement>("[data-create-server-description]");
const createServerEnabled = requireElement<HTMLInputElement>("[data-create-server-enabled]");
const createServerGeneralError = requireElement<HTMLElement>("[data-create-server-general-error]");
const createContainerModal = requireElement<HTMLElement>("[data-create-container-modal]");
const createContainerForm = requireElement<HTMLFormElement>("[data-create-container-form]");
const createContainerEndpoint = requireElement<HTMLInputElement>("[data-create-container-endpoint]");
const createContainerDescription = requireElement<HTMLTextAreaElement>("[data-create-container-description]");
const createContainerEnabled = requireElement<HTMLInputElement>("[data-create-container-enabled]");
const createContainerGeneralError = requireElement<HTMLElement>("[data-create-container-general-error]");

document.addEventListener("DOMContentLoaded", () => {
    void initObjectsPage();
});

async function initObjectsPage(): Promise<void> {
    page.addEventListener("click", handlePageClick);
    serverList.addEventListener("click", handleServerSelection);
    containerList.addEventListener("click", handleContainerSelection);
    serverForm.addEventListener("submit", event => void saveServer(event));
    containerForm.addEventListener("submit", event => void saveContainer(event));
    createServerForm.addEventListener("submit", event => void createServer(event));
    createContainerForm.addEventListener("submit", event => void createContainer(event));
    createServerModal.addEventListener("show.bs.modal", resetCreateServerForm);
    createServerModal.addEventListener("shown.bs.modal", () => createServerName.focus());
    createContainerModal.addEventListener("show.bs.modal", resetCreateContainerForm);
    createContainerModal.addEventListener("shown.bs.modal", () => createContainerEndpoint.focus());

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
    fillServerForm(server);
    renderServers();

    const response = await sendJsonRequest<ServiceResultWithData<AdminContainer[]>>(
        `/api/admin/objects/servers/${serverId}/containers`);
    state.containers = unwrapServiceResult(response);
    renderContainers();
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
        const payload = { ...settings, version: requireServer(serverId).version };
        const response = await sendJsonRequest<ServiceResultWithData<AdminServer>>(
            `/api/admin/objects/servers/${serverId}`,
            "PUT",
            payload);
        const savedServer = unwrapServiceResult(response);
        await loadServers(savedServer.id);
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

async function saveContainer(event: SubmitEvent): Promise<void> {
    event.preventDefault();
    clearPageError();
    clearFormErrors(containerForm);
    const settings = readSettings(containerEndpoint, containerDescription, containerEnabled);

    try {
        const serverId = requireSelectedServerId();
        const containerId = requireSelectedContainerId();
        const payload = { ...settings, version: requireContainer(containerId).version };
        const response = await sendJsonRequest<ServiceResultWithData<AdminContainer>>(
            `/api/admin/objects/containers/${containerId}`,
            "PUT",
            payload);
        const savedContainer = unwrapServiceResult(response);
        await selectServer(serverId);
        selectContainer(savedContainer.id);
    }
    catch (error: unknown) {
        showFormOrPageError(error, containerForm, "data-container-error");
    }
}

async function createContainer(event: SubmitEvent): Promise<void> {
    event.preventDefault();
    clearFormErrors(createContainerForm);
    hideModalError(createContainerGeneralError);

    try {
        const serverId = requireSelectedServerId();
        const response = await sendJsonRequest<ServiceResultWithData<AdminContainer>>(
            `/api/admin/objects/servers/${serverId}/containers`,
            "POST",
            readSettings(createContainerEndpoint, createContainerDescription, createContainerEnabled));
        const savedContainer = unwrapServiceResult(response);
        hideModal(createContainerModal);
        await selectServer(serverId);
        selectContainer(savedContainer.id);
    }
    catch (error: unknown) {
        if (!showFormFieldError(error, createContainerForm, "data-create-container-error")) {
            showModalError(createContainerGeneralError, error, "Не удалось создать контейнер.");
        }
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
        { expectedContainerCount: impact.containerCount, version: impact.version });
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
        "DELETE",
        { version: requireContainer(containerId).version });
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

    if (action === "delete-server") {
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
    fillContainerForm(container);
    renderContainers();
}

function renderServers(): void {
    serverList.replaceChildren();
    serverEmpty.classList.toggle("d-none", state.servers.length !== 0);

    for (const server of state.servers) {
        serverList.append(createServerButton(server, server.id === state.selectedServerId));
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

    if (state.selectedContainerId === null) {
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

    const header = document.createElement("span");
    header.classList.add("d-flex", "justify-content-between", "align-items-center", "gap-2");

    const title = document.createElement("span");
    title.classList.add("fw-semibold", "text-truncate");
    title.textContent = item.lastKnownName ?? "Контейнер";

    const details = document.createElement("span");
    details.classList.add("d-block", "small", "text-body-secondary", "text-truncate", "mt-1");
    details.textContent = item.endpointUrl;

    header.append(title, createStatusBadge(item.status));
    button.append(header, details);
    return button;
}

function createServerButton(server: AdminServer, selected: boolean): HTMLButtonElement {
    const button = document.createElement("button");
    button.type = "button";
    button.classList.add("list-group-item", "list-group-item-action");
    button.classList.toggle("active", selected);
    button.dataset.serverId = server.id;

    const header = document.createElement("span");
    header.classList.add("d-flex", "justify-content-between", "align-items-center", "gap-2");

    const title = document.createElement("span");
    title.classList.add("fw-semibold", "text-truncate");
    title.textContent = server.name;

    const details = document.createElement("span");
    details.classList.add("d-block", "small", "text-body-secondary", "text-truncate", "mt-1");
    details.textContent = server.endpointUrl;

    header.append(title, createStatusBadge(server.status));
    button.append(header, details);
    return button;
}

function fillServerForm(server: AdminServer): void {
    selectionPlaceholder.classList.add("d-none");
    serverForm.classList.remove("d-none");
    serverDanger.classList.remove("d-none");
    containersSection.classList.remove("d-none");
    serverFormTitle.textContent = server.name;
    serverName.value = server.name;
    serverEndpoint.value = server.endpointUrl;
    serverDescription.value = server.description ?? "";
    serverEnabled.checked = server.isMonitoringEnabled;
    clearFormErrors(serverForm);
}

function fillContainerForm(container: AdminContainer): void {
    containerForm.classList.remove("d-none");
    containerDanger.classList.remove("d-none");
    containerFormTitle.textContent = container.lastKnownName ?? "Контейнер";
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

function showFormOrPageError(error: unknown, form: HTMLFormElement, errorAttribute: string): void {
    if (!showFormFieldError(error, form, errorAttribute)) {
        showPageError(error);
    }
}

function showFormFieldError(error: unknown, form: HTMLFormElement, errorAttribute: string): boolean {
    if (!(error instanceof ApiError) || !error.field) {
        return false;
    }

    const field = form.querySelector<HTMLInputElement | HTMLTextAreaElement>(`[name="${error.field}"]`);
    const message = form.querySelector<HTMLElement>(`[${errorAttribute}="${error.field}"]`);
    if (!field || !message) {
        return false;
    }

    field.classList.add("is-invalid");
    message.textContent = error.message;
    return true;
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

function requireContainer(containerId: string): AdminContainer {
    const container = state.containers.find(item => item.id === containerId);
    if (!container) {
        throw new Error("Контейнер не найден.");
    }

    return container;
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

function resetCreateContainerForm(): void {
    clearPageError();
    createContainerForm.reset();
    clearFormErrors(createContainerForm);
    hideModalError(createContainerGeneralError);
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
