import { createDataTable, createTableAction } from "../shared/dataTable.js";
import { initializeMonitoringEditor } from "./monitoringEditor.js";
import { selectMonitoringObjects } from "./monitoringSnapshot.js";
import type { ServerType, DashboardSnapshotDto } from "./monitoringSnapshot.js";
import { createSearchableRadioSelect } from "../shared/searchableRadioSelect.js";
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
const statusFilter = requireElement<HTMLSelectElement>("[data-dashboard-status]");
const typeFilter = requireElement<HTMLSelectElement>("[data-dashboard-type]");
let dashboardTable: ReturnType<typeof createDataTable>;
let monitoringEditor: ReturnType<typeof initializeMonitoringEditor> = null;

document.addEventListener("DOMContentLoaded", () => {
    void initDashboard();
});

async function initDashboard(): Promise<void> {
    createSearchableRadioSelect(statusFilter);
    createSearchableRadioSelect(typeFilter);
    monitoringEditor = initializeMonitoringEditor(() => loadSnapshot(false));
    document.querySelector<HTMLButtonElement>("[data-monitoring-create]")?.addEventListener("click", () => {
        void monitoringEditor?.open(null, typeFilter.value === "Application" ? "Application" : "Server");
    });
    content.addEventListener("click", event => {
        if (!(event.target instanceof Element)) return;
        const button = event.target.closest<HTMLButtonElement>("[data-monitoring-edit-id]");
        if (!button) return;
        const type = button.dataset.monitoringKind;
        if (type !== "Server" && type !== "Application") throw new Error("Неизвестный тип объекта.");
        void monitoringEditor?.open(button.dataset.monitoringEditId!, type);
    });
    dashboardTable = createDataTable(content, "monitoring", "Состояние серверов и приложений", [
        { key: "name", title: "Объект", width: 220 },
        { key: "kind", title: "Тип", width: 120 },
        { key: "server", title: "Сервер", width: 200 },
        { key: "internalName", title: "Внутреннее имя", help: "Имя сервера, полученное при проверке. Заданное администратором имя показывается в колонке «Объект»." },
        { key: "status", title: "Статус", width: 160, help: "Результат мониторинга. «Неизвестно» означает, что актуального результата проверки ещё нет." },
        { key: "checked", title: "Последняя проверка", width: 220, help: "Время последней проверки в UTC." },
        { key: "started", title: "Запущен", width: 220, help: "Время запуска сервера или приложения в UTC, полученное при проверке." },
        { key: "uptime", title: "Текущий uptime", width: 180, help: "Продолжительность работы сервера или приложения с последнего запуска. Это не процент доступности за период." },
        { key: "description", title: "Описание", width: 260 },
        ...(monitoringEditor ? [{ key: "actions", title: "Действия", width: 140 }] : [])
    ]);
    statusFilter.addEventListener("change", () => {
        if (state.snapshot !== null) renderDashboard(state.snapshot);
    });
    typeFilter.addEventListener("change", () => {
        if (state.snapshot !== null) renderDashboard(state.snapshot);
    });
    requireElement<HTMLButtonElement>("[data-dashboard-refresh]")
        .addEventListener("click", () => void loadSnapshot(true));
    bindSearch();
    await loadSnapshot(true);
    if (typeof signalR === "undefined") {
        setSignalState("disconnected", "Автообновление недоступно — нажмите «Обновить»");
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
        setSignalState("reconnecting", "Восстанавливаем автообновление…");
    });
    hubConnection.onreconnected(() => {
        setSignalState("connected", "Данные обновляются автоматически");
        cancelScheduledSnapshotRefresh();
        void loadSnapshot(false);
    });
    hubConnection.onclose(() => {
        setSignalState("disconnected", "Автообновление недоступно — нажмите «Обновить»");
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
        setSignalState("connected", "Данные обновляются автоматически");
    }
    catch {
        setSignalState("disconnected", "Автообновление недоступно — нажмите «Обновить»");
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
    const objects = selectMonitoringObjects(snapshot, typeFilter.value, statusFilter.value);
    const rows = objects.map(object => dashboardTable.row([
        object.name, object.type === "Server" ? "Сервер" : "Приложение", object.serverName,
        object.internalName ?? "Нет данных", createStatusBadge(object.status),
        formatUtc(object.lastCheckedAtUtc), formatUtc(object.startedAtUtc),
        formatDuration(object.uptimeSeconds), object.description ?? "—", ...editAction(object.id, object.type)
    ]));

    dashboardTable.body.replaceChildren(...rows);
    if (rows.length === 0) {
        const filtered = state.search !== "" || statusFilter.value !== "" || typeFilter.value !== "";
        renderMessage(
            filtered ? "no-results" : "empty",
            filtered
                ? "По выбранным фильтрам ничего не найдено."
                : "Доступных объектов мониторинга пока нет.");
        return;
    }

    stateContainer.hidden = true;
    content.hidden = false;
    dashboardTable.refresh();
}

function editAction(id: string, type: ServerType): HTMLButtonElement[] {
    if (!monitoringEditor) return [];
    const button = createTableAction("Изменить");
    button.dataset.monitoringEditId = id;
    button.dataset.monitoringKind = type;
    return [button];
}

function createStatusBadge(status: MonitoringStatus): HTMLElement {
    const badge = document.createElement("span");
    badge.classList.add("badge");

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
