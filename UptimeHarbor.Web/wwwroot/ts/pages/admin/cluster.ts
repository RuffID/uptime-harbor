import { ApiError, isAbortError } from "../../shared/http.js";
import { clusterApi } from "./clusterApi.js";
import { LatestRequest } from "./clusterDraft.js";
import { AdministrationRefresh } from "./clusterRefresh.js";
import { ClusterEditor } from "./clusterEditor.js";
import { ClusterViews } from "./clusterViews.js";
import { kindNames, operationText, delegateActions } from "./clusterDom.js";
import type { AccountChange, Change, Diagnostic, EntityKind, Pending, Snapshot, LifecycleChange } from "./clusterModels.js";

interface BootstrapWindow extends Window {
    bootstrap?: { Modal: { getOrCreateInstance(element: Element): { show(): void; hide(): void } } };
}

function initialize(): void {
    const root = document.querySelector<HTMLElement>("[data-cluster-page]");
    if (!root || root.dataset.initialized === "true") return;
    root.dataset.initialized = "true";
    delegateActions(root);
    const require = <T extends HTMLElement>(key: string): T => {
        const node = root.querySelector<T>(`[data-cluster-${key}]`);
        if (!node) throw new Error(`Отсутствует элемент ${key}.`);
        return node;
    };
    const error = require<HTMLElement>("error");
    const status = require<HTMLElement>("status");
    const outcome = require<HTMLElement>("outcome");
    const kindSelect = require<HTMLSelectElement>("kind");
    const create = require<HTMLButtonElement>("create");
    const previous = require<HTMLButtonElement>("previous");
    const next = require<HTMLButtonElement>("next");
    const pager = require<HTMLElement>("pager");
    const latest = new LatestRequest();
    const refreshRequests = new AdministrationRefresh(clusterApi);
    const cancelIds = new Map<string, string>();
    const lifecycleCancels = new Map<string, LifecycleChange>();
    let snapshot: Snapshot | null = null;
    let pending: Pending[] = [];
    let diagnostic: Diagnostic | null = null;
    let offset = 0;
    let stopped = false;
    let denied = false;
    let timer: ReturnType<typeof setTimeout> | null = null;
    let hub: signalR.HubConnection | null = null;
    for (const [kind, name] of Object.entries(kindNames)) {
        const option = document.createElement("option"); option.value = kind; option.textContent = name;
        kindSelect.append(option);
    }
    const initialKind = root.dataset.clusterPage;
    if (!initialKind || !(initialKind in kindNames)) throw new Error("Неизвестный раздел управления сеткой.");
    kindSelect.value = initialKind;
    const current = (): Snapshot => {
        if (!snapshot || denied) throw new Error("Требуется повторная авторизация и загрузка принятого состояния.");
        return snapshot;
    };
    const selectedKind = (): EntityKind => kindSelect.value as EntityKind;
    const failure = (value: unknown): void => {
        if (isAbortError(value)) return;
        if (value instanceof ApiError && (value.status === 401 || value.status === 403)) {
            denied = true; latest.invalidate(); refreshRequests.invalidate(); snapshot = null; pending = []; diagnostic = null;
            editor.close(); views.clear(); outcome.textContent = ""; create.disabled = true;
            status.textContent = "Защищённый доступ закрыт. Войдите действующим аккаунтом заново.";
            if (timer) clearTimeout(timer);
            void hub?.stop();
        }
        error.textContent = value instanceof Error ? value.message : "Не удалось обновить данные. Последнее известное состояние может быть устаревшим.";
    };
    const refresh = async (): Promise<void> => {
        if (stopped || denied) throw new Error("Защищённый доступ закрыт.");
        try {
            const response = await refreshRequests.read(offset);
            if (stopped || !response) return;
            const [accepted, local, health] = response;
            if (snapshot && snapshot.nodeId === accepted.nodeId && accepted.revision < snapshot.revision) {
                throw new Error("Получено устаревшее состояние. Редактор сохранён; повторите обновление.");
            }
            snapshot = accepted; pending = local; diagnostic = health;
            error.textContent = "";
            status.textContent = `Принятое состояние: revision ${accepted.revision}, ${new Date(accepted.atUtc).toLocaleString()}. Обновление повторно проверило активного Administrator.`;
            create.disabled = false; previous.disabled = offset === 0; next.disabled = pending.length < 100;
            pager.textContent = `Локальные записи ${offset + 1}–${offset + pending.length} (по 100)`;
            views.render(accepted, pending, health, selectedKind());
        }
        catch (value: unknown) {
            failure(value);
            if (!denied) status.textContent = "Обновление не подтверждено. Показаны последние известные данные; ввод редактора сохранён.";
            throw value;
        }
    };
    const quietRefresh = (): void => { void refresh().catch(() => { /* Ошибка обработана текущим запросом. */ }); };
    const modal = require<HTMLElement>("modal");
    const bootstrap = (window as BootstrapWindow).bootstrap;
    if (!bootstrap) throw new Error("Bootstrap не загружен.");
    const modalController = bootstrap.Modal.getOrCreateInstance(modal);
    const editor = new ClusterEditor(require<HTMLElement>("editor"), {
        snapshot: current, refresh, failure, show: () => modalController.show(), hide: () => modalController.hide(),
        submit: (body, account) => account ? clusterApi.account(body as AccountChange) : clusterApi.change(body as Change)
    });
    modal.addEventListener("hide.bs.modal", event => { if (editor.isSaving) event.preventDefault(); });
    modal.addEventListener("hidden.bs.modal", () => { editor.close(); create.focus(); });
    const views = new ClusterViews(root, {
        edit: (kind, entity, action, local, conflict, useCurrent) => editor.open(kind, entity, action, local ?? null, conflict ?? null, useCurrent),
        cancel: row => {
            if (!window.confirm("Запросить отмену непринятой заявки? Если принятие уже выиграло гонку, настройки сохранятся; результат покажет настоящий исход.")) return;
            let id = cancelIds.get(row.operationId);
            if (!id) { id = crypto.randomUUID(); cancelIds.set(row.operationId, id); }
            const request = latest.begin();
            if (row.kind === "Node" && (!diagnostic || diagnostic.membershipVersion === null)) {
                failure(new Error("Сначала обновите диагностику состава сетки.")); return;
            }
            let submission;
            if (row.kind === "Node") {
                let body = lifecycleCancels.get(row.operationId);
                if (!body) {
                    body = { operationId: id, action: "Cancel", nodeId: row.entityId,
                        baseRevision: diagnostic!.operationsRevision, membershipVersion: diagnostic!.membershipVersion!, targetOperationId: row.operationId };
                    lifecycleCancels.set(row.operationId, body);
                }
                submission = clusterApi.lifecycle(body);
            }
            else submission = clusterApi.cancel(row.operationId, id);
            void submission.then(result => {
                if (denied || stopped || !latest.isCurrent(request.version)) return;
                outcome.textContent = operationText(result); quietRefresh();
            }).catch(value => { if (latest.isCurrent(request.version)) failure(value); });
        },
        find: id => {
            const request = latest.begin();
            void clusterApi.find(id, request.signal).then(result => {
                if (!stopped && !denied && latest.isCurrent(request.version)) outcome.textContent = operationText(result);
            }).catch(value => { if (latest.isCurrent(request.version)) failure(value); });
        }
    });
    create.addEventListener("click", () => editor.open(selectedKind(), null));
    kindSelect.addEventListener("change", () => { if (snapshot && diagnostic) views.render(snapshot, pending, diagnostic, selectedKind()); });
    require<HTMLButtonElement>("refresh").addEventListener("click", quietRefresh);
    previous.addEventListener("click", () => { offset = Math.max(0, offset - 100); quietRefresh(); });
    next.addEventListener("click", () => { offset += 100; quietRefresh(); });
    const poll = (): void => {
        timer = setTimeout(() => {
            if (stopped || denied) return;
            void refresh().catch(() => { }).finally(() => { if (!stopped && !denied) poll(); });
        }, 10000);
    };
    const connect = async (): Promise<void> => {
        if (typeof signalR === "undefined") return;
        hub = new signalR.HubConnectionBuilder().withUrl("/hubs/monitoring")
            .withAutomaticReconnect([0, 2000, 10000, 30000]).configureLogging(signalR.LogLevel.Warning).build();
        const connection = hub;
        let coalesced: ReturnType<typeof setTimeout> | null = null;
        const changed = (): void => {
            if (coalesced !== null) clearTimeout(coalesced);
            coalesced = setTimeout(() => { coalesced = null; if (!stopped && !denied) quietRefresh(); }, 200);
        };
        connection.on("ObjectAdministrationChanged", changed);
        connection.on("MonitoringObjectChanged", changed);
        connection.onreconnecting(() => {
            latest.invalidate(); refreshRequests.invalidate();
            status.textContent = "Связь восстанавливается; показаны последние известные данные.";
        });
        connection.onreconnected(() => { quietRefresh(); });
        connection.onclose(() => { if (!stopped && !denied) quietRefresh(); });
        try { await connection.start(); if (!stopped && !denied) quietRefresh(); }
        catch (value: unknown) { if (!stopped && !denied) { quietRefresh(); } }
    };
    window.addEventListener("pagehide", () => {
        stopped = true; latest.invalidate(); refreshRequests.invalidate(); editor.close(); cancelIds.clear(); lifecycleCancels.clear();
        if (timer) clearTimeout(timer);
        void hub?.stop();
    });
    window.addEventListener("pageshow", event => {
        if (!event.persisted || denied) return;
        stopped = false; quietRefresh(); poll(); void connect();
    });
    quietRefresh(); poll(); void connect();
}

if (document.readyState === "loading") document.addEventListener("DOMContentLoaded", initialize, { once: true });
else initialize();
