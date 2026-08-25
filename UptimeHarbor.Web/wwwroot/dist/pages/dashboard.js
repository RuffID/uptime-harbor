import { sendJsonRequest, unwrapServiceResult } from "../shared/http.js";
var MonitoringStatus;
(function (MonitoringStatus) {
    MonitoringStatus[MonitoringStatus["Unknown"] = 0] = "Unknown";
    MonitoringStatus[MonitoringStatus["Available"] = 1] = "Available";
    MonitoringStatus[MonitoringStatus["Unavailable"] = 2] = "Unavailable";
})(MonitoringStatus || (MonitoringStatus = {}));
const state = {
    search: "",
    requestVersion: 0,
    snapshot: null
};
let searchTimer = null;
let reconnectTimer = null;
let connection = null;
const content = requireElement("[data-dashboard-content]");
const stateContainer = requireElement("[data-dashboard-state]");
const searchInput = requireElement("[data-dashboard-search]");
const signalState = requireElement("[data-signal-state]");
const signalStateText = requireElement("[data-signal-state-text]");
document.addEventListener("DOMContentLoaded", () => {
    void initDashboard();
});
async function initDashboard() {
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
function bindSearch() {
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
function configureSignalR(hubConnection) {
    hubConnection.on("MonitoringObjectChanged", (...args) => {
        validateMonitoringMessage(args[0]);
        void loadSnapshot(false);
    });
    hubConnection.on("ObjectAdministrationChanged", () => {
        void loadSnapshot(false);
    });
    hubConnection.onreconnecting(() => {
        setSignalState("reconnecting", "Связь потеряна, переподключаемся…");
    });
    hubConnection.onreconnected(() => {
        setSignalState("connected", "Обновления подключены");
        void loadSnapshot(false);
    });
    hubConnection.onclose(() => {
        setSignalState("disconnected", "Обновления временно недоступны");
        scheduleSignalRStart();
    });
}
async function startSignalR() {
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
function scheduleSignalRStart() {
    if (reconnectTimer !== null) {
        return;
    }
    reconnectTimer = window.setTimeout(() => {
        reconnectTimer = null;
        void startSignalR();
    }, 5_000);
}
async function loadSnapshot(showLoading) {
    const requestVersion = ++state.requestVersion;
    if (showLoading) {
        renderMessage("loading", "Загружаем актуальное состояние…");
    }
    try {
        const query = state.search ? `?search=${encodeURIComponent(state.search)}` : "";
        const response = await sendJsonRequest(`/api/dashboard${query}`);
        const snapshot = unwrapServiceResult(response);
        if (requestVersion !== state.requestVersion) {
            return;
        }
        state.snapshot = snapshot;
        renderDashboard(snapshot);
    }
    catch (error) {
        if (requestVersion !== state.requestVersion) {
            return;
        }
        const message = error instanceof Error ? error.message : "Не удалось загрузить панель.";
        renderMessage("error", message);
    }
}
function renderDashboard(snapshot) {
    content.replaceChildren();
    if (snapshot.servers.length === 0) {
        renderMessage(state.search ? "no-results" : "empty", state.search
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
function createServerCard(server) {
    const article = document.createElement("article");
    article.classList.add("card", "server-card", "border-secondary-subtle");
    const body = document.createElement("div");
    body.classList.add("card-body", "p-3", "p-md-4");
    const header = document.createElement("div");
    header.classList.add("server-card__header", "mb-3");
    const titleBlock = document.createElement("div");
    const title = document.createElement("h2");
    title.classList.add("h4", "object-name", "mb-1");
    title.textContent = server.displayName;
    titleBlock.append(title, createDescription(server.description));
    header.append(titleBlock, createStatusBadge(server.status));
    body.append(header, createMetadata([
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
        list.classList.add("container-list");
        for (const container of server.containers) {
            list.append(createContainerCard(container));
        }
        body.append(divider, containerTitle, list);
    }
    article.append(body);
    return article;
}
function createContainerCard(container) {
    const article = document.createElement("article");
    article.classList.add("container-card");
    const header = document.createElement("div");
    header.classList.add("server-card__header", "mb-3");
    const titleBlock = document.createElement("div");
    const title = document.createElement("h4");
    title.classList.add("h5", "object-name", "mb-1");
    title.textContent = container.displayName;
    titleBlock.append(title, createDescription(container.description));
    header.append(titleBlock, createStatusBadge(container.status));
    article.append(header, createMetadata([
        ["Последняя проверка", formatUtc(container.lastCheckedAtUtc)],
        ["Запущен", formatUtc(container.startedAtUtc)],
        ["Текущий uptime", formatDuration(container.uptimeSeconds)]
    ]));
    return article;
}
function createDescription(description) {
    const paragraph = document.createElement("p");
    paragraph.classList.add("object-description", "text-secondary", "mb-0");
    paragraph.textContent = description ?? "Описание не задано";
    return paragraph;
}
function createStatusBadge(status) {
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
function createMetadata(items) {
    const container = document.createElement("div");
    container.classList.add("object-metadata");
    for (const [labelText, valueText] of items) {
        const item = document.createElement("div");
        item.classList.add("metadata-item");
        const label = document.createElement("span");
        label.classList.add("metadata-label");
        label.textContent = labelText;
        const value = document.createElement("span");
        value.textContent = valueText;
        item.append(label, value);
        container.append(item);
    }
    return container;
}
function renderMessage(kind, message) {
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
function setSignalState(stateName, message) {
    signalState.dataset.state = stateName;
    signalStateText.textContent = message;
}
function formatUtc(value) {
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
function formatDuration(totalSeconds) {
    if (totalSeconds === null) {
        return "Нет данных";
    }
    if (!Number.isSafeInteger(totalSeconds) || totalSeconds < 0) {
        throw new Error("Сервер вернул некорректный uptime.");
    }
    const days = Math.floor(totalSeconds / 86_400);
    const hours = Math.floor(totalSeconds % 86_400 / 3_600);
    const minutes = Math.floor(totalSeconds % 3_600 / 60);
    const parts = [];
    if (days > 0) {
        parts.push(`${days} д`);
    }
    if (hours > 0 || days > 0) {
        parts.push(`${hours} ч`);
    }
    parts.push(`${minutes} мин`);
    return parts.join(" ");
}
function validateMonitoringMessage(value) {
    if (typeof value !== "object" || value === null) {
        throw new Error("Получено некорректное событие мониторинга.");
    }
    const candidate = value;
    if (typeof candidate.objectId !== "string" || typeof candidate.objectKind !== "number") {
        throw new Error("Получено неполное событие мониторинга.");
    }
    return candidate;
}
function requireElement(selector) {
    const element = document.querySelector(selector);
    if (element === null) {
        throw new Error(`Обязательный элемент не найден: ${selector}`);
    }
    return element;
}
