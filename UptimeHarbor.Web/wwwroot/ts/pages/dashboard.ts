import {
    isAbortError,
    sendJsonRequest,
    ServiceResultWithData,
    unwrapServiceResult
} from "../shared/http.js";

enum MonitoringStatus {
    Unknown = 0,
    Available = 1,
    Unavailable = 2
}

interface DashboardContainerDto {
    id: string;
    serverId: string;
    displayName: string;
    description: string | null;
    status: MonitoringStatus;
    lastCheckedAtUtc: string | null;
    startedAtUtc: string | null;
    uptimeSeconds: number | null;
}

interface DashboardServerDto {
    id: string;
    name: string;
    internalName: string | null;
    description: string | null;
    status: MonitoringStatus;
    lastCheckedAtUtc: string | null;
    containers: DashboardContainerDto[];
}

interface DashboardSnapshotDto {
    generatedAtUtc: string;
    servers: DashboardServerDto[];
}

interface MonitoringObjectChangedMessage {
    objectId: string;
    objectKind: number;
}

interface DashboardState {
    search: string;
    requestVersion: number;
    snapshot: DashboardSnapshotDto | null;
}

const state: DashboardState = {
    search: "",
    requestVersion: 0,
    snapshot: null
};

// Окно 200 мс объединяет пачку monitoring-событий в один snapshot-запрос.
const SNAPSHOT_REFRESH_DEBOUNCE_MS = 200;
let searchTimer: number | null = null;
let snapshotRefreshTimer: number | null = null;
let reconnectTimer: number | null = null;
let snapshotAbortController: AbortController | null = null;
let connection: signalR.HubConnection | null = null;

const content = requireElement<HTMLElement>("[data-dashboard-content]");
const stateContainer = requireElement<HTMLElement>("[data-dashboard-state]");
const searchInput = requireElement<HTMLInputElement>("[data-dashboard-search]");
const signalState = requireElement<HTMLElement>("[data-signal-state]");
const signalStateText = requireElement<HTMLElement>("[data-signal-state-text]");

document.addEventListener("DOMContentLoaded", () => {
    void initDashboard();
});

async function initDashboard(): Promise<void> {
    bindSearch();
    await loadSnapshot(true);
    if (typeof signalR === "undefined") {
        setSignalState("disconnected", "Обновления временно недоступны");
        return;
    }

    connection = new signalR.HubConnectionBuilder()
        .withUrl("/hubs/monitoring")
        .withAutomaticReconnect([0, 2_000, 10_000, 30_000])
        .configureLogging(signalR.LogLevel.Warning)
        .build();
    configureSignalR(connection);
    await startSignalR();
}

function bindSearch(): void {
    searchInput.addEventListener("input", () => {
        state.search = searchInput.value.trim();
        if (searchTimer !== null) {
            window.clearTimeout(searchTimer);
        }

        searchTimer = window.setTimeout(() => {
            searchTimer = null;
            void loadSnapshot(true);
        }, 250);
    });
}

function configureSignalR(hubConnection: signalR.HubConnection): void {
    hubConnection.on("MonitoringObjectChanged", (...args: unknown[]) => {
        validateMonitoringMessage(args[0]);
        scheduleSnapshotRefresh();
    });
    hubConnection.on("ObjectAdministrationChanged", () => {
        scheduleSnapshotRefresh();
    });
    hubConnection.onreconnecting(() => {
        setSignalState("reconnecting", "Связь потеряна, переподключаемся…");
    });
    hubConnection.onreconnected(() => {
        setSignalState("connected", "Обновления подключены");
        cancelScheduledSnapshotRefresh();
        void loadSnapshot(false);
    });
    hubConnection.onclose(() => {
        setSignalState("disconnected", "Обновления временно недоступны");
        scheduleSignalRStart();
    });
}

function scheduleSnapshotRefresh(): void {
    if (snapshotRefreshTimer !== null) {
        window.clearTimeout(snapshotRefreshTimer);
    }

    snapshotRefreshTimer = window.setTimeout(() => {
        snapshotRefreshTimer = null;
        void loadSnapshot(false);
    }, SNAPSHOT_REFRESH_DEBOUNCE_MS);
}

function cancelScheduledSnapshotRefresh(): void {
    if (snapshotRefreshTimer === null) {
        return;
    }

    window.clearTimeout(snapshotRefreshTimer);
    snapshotRefreshTimer = null;
}

async function startSignalR(): Promise<void> {
    if (connection === null) {
        return;
    }

    if (reconnectTimer !== null) {
        window.clearTimeout(reconnectTimer);
        reconnectTimer = null;
    }

    try {
        await connection.start();
        setSignalState("connected", "Обновления подключены");
    }
    catch {
        setSignalState("disconnected", "Обновления временно недоступны");
        scheduleSignalRStart();
    }
}

function scheduleSignalRStart(): void {
    if (reconnectTimer !== null) {
        return;
    }

    reconnectTimer = window.setTimeout(() => {
        reconnectTimer = null;
        void startSignalR();
    }, 5_000);
}

async function loadSnapshot(showLoading: boolean): Promise<void> {
    const requestVersion = ++state.requestVersion;
    snapshotAbortController?.abort();
    const abortController = new AbortController();
    snapshotAbortController = abortController;
    if (showLoading) {
        renderMessage("loading", "Загружаем актуальное состояние…");
    }

    try {
        const query = state.search ? `?search=${encodeURIComponent(state.search)}` : "";
        const response = await sendJsonRequest<ServiceResultWithData<DashboardSnapshotDto>>(
            `/api/dashboard${query}`,
            "GET",
            null,
            abortController.signal);
        const snapshot = unwrapServiceResult(response);
        if (requestVersion !== state.requestVersion) {
            return;
        }

        state.snapshot = snapshot;
        renderDashboard(snapshot);
    }
    catch (error: unknown) {
        if (isAbortError(error)) {
            return;
        }

        if (requestVersion !== state.requestVersion) {
            return;
        }

        const message = error instanceof Error ? error.message : "Не удалось загрузить панель.";
        renderMessage("error", message);
    }
    finally {
        if (snapshotAbortController === abortController) {
            snapshotAbortController = null;
        }
    }
}

function renderDashboard(snapshot: DashboardSnapshotDto): void {
    content.replaceChildren();
    if (snapshot.servers.length === 0) {
        renderMessage(
            state.search ? "no-results" : "empty",
            state.search
                ? "По вашему запросу ничего не найдено."
                : "Доступных объектов мониторинга пока нет.");
        return;
    }

    for (const server of snapshot.servers) {
        content.append(createServerCard(server));
    }

    stateContainer.hidden = true;
    content.hidden = false;
}

function createServerCard(server: DashboardServerDto): HTMLElement {
    const article = document.createElement("article");
    article.classList.add("card", "border-0", "shadow-sm");

    const body = document.createElement("div");
    body.classList.add("card-body", "p-3", "p-md-4");

    const header = document.createElement("div");
    header.classList.add("d-flex", "flex-wrap", "justify-content-between", "align-items-start", "gap-3", "mb-3");
    const titleBlock = document.createElement("div");
    const title = document.createElement("h2");
    title.classList.add("h4", "mb-1");
    title.textContent = server.name;
    titleBlock.append(title);
    const description = createDescription(server.description);
    if (description !== null) {
        titleBlock.append(description);
    }
    header.append(titleBlock, createStatusBadge(server.status));

    body.append(
        header,
        createMetadata([
            ...(server.internalName === null
                ? []
                : [["Внутреннее имя", server.internalName] as const]),
            ["Последняя проверка", formatUtc(server.lastCheckedAtUtc)],
            ["Контейнеры", server.containers.length.toString()]
        ]));

    if (server.containers.length > 0) {
        const divider = document.createElement("hr");
        divider.classList.add("my-4", "border-secondary-subtle");
        const containerTitle = document.createElement("h3");
        containerTitle.classList.add("h6", "text-uppercase", "text-secondary", "mb-3");
        containerTitle.textContent = "Контейнеры";
        const list = document.createElement("div");
        list.classList.add("d-grid", "gap-2");
        for (const container of server.containers) {
            list.append(createContainerCard(container));
        }

        body.append(divider, containerTitle, list);
    }

    article.append(body);
    return article;
}

function createContainerCard(container: DashboardContainerDto): HTMLElement {
    const article = document.createElement("article");
    article.classList.add("border", "border-secondary-subtle", "rounded-3", "bg-body-tertiary", "p-3");

    const header = document.createElement("div");
    header.classList.add("d-flex", "flex-wrap", "justify-content-between", "align-items-start", "gap-3", "mb-3");
    const titleBlock = document.createElement("div");
    const title = document.createElement("h4");
    title.classList.add("h5", "mb-1");
    title.textContent = container.displayName;
    titleBlock.append(title);
    const description = createDescription(container.description);
    if (description !== null) {
        titleBlock.append(description);
    }
    header.append(titleBlock, createStatusBadge(container.status));

    article.append(
        header,
        createMetadata([
            ["Последняя проверка", formatUtc(container.lastCheckedAtUtc)],
            ["Запущен", formatUtc(container.startedAtUtc)],
            ["Текущий uptime", formatDuration(container.uptimeSeconds)]
        ]));
    return article;
}

function createDescription(description: string | null): HTMLElement | null {
    if (description === null) {
        return null;
    }

    const paragraph = document.createElement("p");
    paragraph.classList.add("text-body-secondary", "mb-0");
    paragraph.textContent = description;
    return paragraph;
}

function createStatusBadge(status: MonitoringStatus): HTMLElement {
    const badge = document.createElement("span");
    badge.classList.add("badge", "rounded-pill", "px-3", "py-2");

    switch (status) {
        case MonitoringStatus.Available:
            badge.classList.add("text-bg-success");
            badge.textContent = "Доступен";
            break;
        case MonitoringStatus.Unavailable:
            badge.classList.add("text-bg-danger");
            badge.textContent = "Недоступен";
            break;
        case MonitoringStatus.Unknown:
            badge.classList.add("text-bg-secondary");
            badge.textContent = "Неизвестно";
            break;
        default:
            throw new Error(`Неизвестный статус мониторинга: ${String(status)}`);
    }

    return badge;
}

function createMetadata(items: ReadonlyArray<readonly [string, string]>): HTMLElement {
    const container = document.createElement("div");
    container.classList.add("row", "row-cols-1", "row-cols-sm-3", "g-3", "small");
    for (const [labelText, valueText] of items) {
        const item = document.createElement("div");
        item.classList.add("col", "d-flex", "flex-column", "gap-1");
        const label = document.createElement("span");
        label.classList.add("text-body-secondary");
        label.textContent = labelText;
        const value = document.createElement("span");
        value.textContent = valueText;
        item.append(label, value);
        container.append(item);
    }

    return container;
}

function renderMessage(kind: "loading" | "empty" | "no-results" | "error", message: string): void {
    const body = document.createElement("div");
    body.classList.add("card-body", "py-5", "text-center");

    if (kind === "loading") {
        const spinner = document.createElement("div");
        spinner.classList.add("spinner-border", "text-info", "mb-3");
        spinner.setAttribute("aria-hidden", "true");
        body.append(spinner);
    }

    const title = document.createElement("p");
    title.classList.add("mb-0");
    if (kind === "error") {
        title.classList.add("text-danger");
    }
    title.textContent = message;
    body.append(title);

    stateContainer.dataset.state = kind;
    stateContainer.replaceChildren(body);
    stateContainer.hidden = false;
    content.hidden = true;
}

function setSignalState(stateName: string, message: string): void {
    signalState.dataset.state = stateName;
    signalStateText.textContent = message;
    signalState.classList.remove("text-bg-success", "text-bg-warning", "text-bg-secondary");

    if (stateName === "connected") {
        signalState.classList.add("text-bg-success");
    }
    else if (stateName === "reconnecting") {
        signalState.classList.add("text-bg-warning");
    }
    else {
        signalState.classList.add("text-bg-secondary");
    }
}

function formatUtc(value: string | null): string {
    if (value === null) {
        return "Нет данных";
    }

    const date = new Date(value);
    if (Number.isNaN(date.getTime()) || !/(Z|\+00:00)$/.test(value)) {
        throw new Error("Сервер вернул некорректное UTC-время.");
    }

    return new Intl.DateTimeFormat("ru-RU", {
        dateStyle: "medium",
        timeStyle: "medium",
        timeZone: "UTC"
    }).format(date) + " UTC";
}

function formatDuration(totalSeconds: number | null): string {
    if (totalSeconds === null) {
        return "Нет данных";
    }
    if (!Number.isSafeInteger(totalSeconds) || totalSeconds < 0) {
        throw new Error("Сервер вернул некорректный uptime.");
    }

    const days = Math.floor(totalSeconds / 86_400);
    const hours = Math.floor(totalSeconds % 86_400 / 3_600);
    const minutes = Math.floor(totalSeconds % 3_600 / 60);
    const parts: string[] = [];
    if (days > 0) {
        parts.push(`${days} д`);
    }
    if (hours > 0 || days > 0) {
        parts.push(`${hours} ч`);
    }
    parts.push(`${minutes} мин`);
    return parts.join(" ");
}

function validateMonitoringMessage(value: unknown): MonitoringObjectChangedMessage {
    if (typeof value !== "object" || value === null) {
        throw new Error("Получено некорректное событие мониторинга.");
    }

    const candidate = value as Partial<MonitoringObjectChangedMessage>;
    if (typeof candidate.objectId !== "string" || typeof candidate.objectKind !== "number") {
        throw new Error("Получено неполное событие мониторинга.");
    }

    return candidate as MonitoringObjectChangedMessage;
}

function requireElement<TElement extends Element>(selector: string): TElement {
    const element = document.querySelector<TElement>(selector);
    if (element === null) {
        throw new Error(`Обязательный элемент не найден: ${selector}`);
    }

    return element;
}
