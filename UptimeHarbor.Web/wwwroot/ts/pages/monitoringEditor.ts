import { createSearchableRadioSelect } from "../shared/searchableRadioSelect.js";
import { ApiError, sendJsonRequest, unwrapServiceResult, unwrapServiceSuccess } from "../shared/http.js";
import type { ServiceResult, ServiceResultWithData } from "../shared/http.js";
import type { ServerType } from "./monitoringSnapshot.js";
interface EditableObject {
    id: string; name: string; serverId?: string | null; endpointUrl: string;
    description: string | null; isMonitoringEnabled: boolean; version: number;
}
interface BootstrapWindow extends Window {
    bootstrap?: { Modal: { getOrCreateInstance(element: Element): { show(): void; hide(): void } } };
}

export function initializeMonitoringEditor(refresh: () => Promise<void>): {
    open(id: string | null, type: ServerType): Promise<void>;
} | null {
    const modalElement = document.querySelector<HTMLElement>("[data-monitoring-modal]");
    if (!modalElement) return null;
    const require = <T extends HTMLElement>(name: string): T => {
        const element = modalElement.querySelector<T>(`[data-monitoring-${name}]`);
        if (!element) throw new Error(`Отсутствует элемент редактора: ${name}.`);
        return element;
    };
    const form = require<HTMLFormElement>("form");
    const fields = require<HTMLFieldSetElement>("fields");
    const type = require<HTMLSelectElement>("type");
    const name = require<HTMLInputElement>("name");
    const endpoint = require<HTMLInputElement>("endpoint");
    const description = require<HTMLTextAreaElement>("description");
    const enabled = require<HTMLInputElement>("enabled");
    const parent = require<HTMLSelectElement>("parent");
    const parentField = require<HTMLElement>("parent-field");
    const error = require<HTMLElement>("error");
    const title = require<HTMLElement>("title");
    const remove = require<HTMLButtonElement>("delete");
    const submit = require<HTMLButtonElement>("save");
    const parentSelect = createSearchableRadioSelect(parent);
    const bootstrap = (window as BootstrapWindow).bootstrap;
    if (!bootstrap) throw new Error("Bootstrap modal недоступен.");
    const modal = bootstrap.Modal.getOrCreateInstance(modalElement);
    let selected: EditableObject | null = null;
    let selectedType: ServerType = "Server";
    let busy = false;
    let loaded = false;
    let openVersion = 0;

    const showType = (): void => {
        parentField.hidden = type.value !== "Application";
        if (parentField.hidden) parent.value = "";
        parentSelect.refresh();
    };
    const setBusy = (value: boolean): void => {
        busy = value;
        fields.disabled = value;
        submit.disabled = value || !loaded;
        remove.disabled = value || selected === null;
    };
    const showError = (failure: unknown): void => {
        error.textContent = failure instanceof Error ? failure.message : "Не удалось выполнить операцию.";
        if (failure instanceof ApiError && failure.field) {
            const input = form.elements.namedItem(failure.field);
            if (input instanceof HTMLElement) input.focus();
        }
    };
    const route = (): string => {
        if (type.value === "Server") return "/api/admin/objects/servers";
        if (type.value === "Application") return "/api/admin/objects/containers";
        throw new Error("Неизвестный тип объекта.");
    };
    const save = async (): Promise<void> => {
        setBusy(true);
        error.textContent = "";
        try {
            const url = route() + (selected ? `/${selected.id}` : "");
            const result = await sendJsonRequest<ServiceResultWithData<EditableObject>>(url, selected ? "PUT" : "POST", {
                name: name.value.trim(), endpointUrl: endpoint.value.trim(), description: description.value.trim() || null,
                isMonitoringEnabled: enabled.checked, version: selected?.version ?? null,
                serverId: type.value === "Application" ? parent.value || null : null
            });
            unwrapServiceResult(result);
            await refresh();
            setBusy(false);
            modal.hide();
        }
        catch (failure: unknown) { showError(failure); }
        finally { setBusy(false); }
    };
    const deleteObject = async (): Promise<void> => {
        if (!selected || busy) return;
        setBusy(true);
        error.textContent = "";
        try {
            let body: object = { version: selected.version };
            let message = "Удалить приложение?";
            if (selectedType === "Server") {
                const impact = unwrapServiceResult(await sendJsonRequest<ServiceResultWithData<{
                    containerCount: number; version: number;
                }>>(`${route()}/${selected.id}/deletion-impact`));
                body = { version: impact.version, expectedContainerCount: impact.containerCount };
                message = `Удалить сервер? Связанных приложений: ${impact.containerCount}. Они сохранятся без связи с сервером.`;
            }
            if (!window.confirm(message)) return;
            unwrapServiceSuccess(await sendJsonRequest<ServiceResult>(`${route()}/${selected.id}`, "DELETE", body));
            await refresh();
            setBusy(false);
            modal.hide();
        }
        catch (failure: unknown) { showError(failure); }
        finally { setBusy(false); }
    };
    type.addEventListener("change", showType);
    form.addEventListener("submit", event => {
        event.preventDefault();
        if (loaded && !busy && form.reportValidity()) void save();
    });
    remove.addEventListener("click", () => void deleteObject());
    modalElement.addEventListener("hide.bs.modal", event => {
        if (busy) event.preventDefault();
        else openVersion++;
    });
    modalElement.addEventListener("hidden.bs.modal", () => {
        parentSelect.close();
        const trigger = document.querySelector<HTMLButtonElement>(`[data-monitoring-edit-id="${selected?.id}"]`)
            ?? document.querySelector<HTMLButtonElement>("[data-monitoring-create]");
        trigger?.focus();
    });

    return {
        async open(id: string | null, objectType: ServerType): Promise<void> {
            if (busy) return;
            const version = ++openVersion;
            selected = null;
            loaded = false;
            selectedType = objectType;
            form.reset();
            type.value = objectType;
            type.disabled = id !== null;
            remove.hidden = true;
            error.textContent = "";
            title.textContent = id === null ? "Создать объект" : "Изменить объект";
            setBusy(true);
            modal.show();
            try {
                const [serversResponse, applicationsResponse] = await Promise.all([
                    sendJsonRequest<ServiceResultWithData<EditableObject[]>>("/api/admin/objects/servers"),
                    sendJsonRequest<ServiceResultWithData<EditableObject[]>>("/api/admin/objects/containers")
                ]);
                if (version !== openVersion) return;
                const servers = unwrapServiceResult(serversResponse);
                const applications = unwrapServiceResult(applicationsResponse);
                parent.replaceChildren();
                const none = document.createElement("option");
                none.value = "";
                none.textContent = "Без сервера";
                parent.append(none);
                for (const server of servers) {
                    const option = document.createElement("option");
                    option.value = server.id;
                    option.textContent = server.name;
                    parent.append(option);
                }
                if (id !== null) {
                    selected = (objectType === "Server" ? servers : applications).find(item => item.id === id) ?? null;
                    if (!selected) throw new Error("Объект уже удалён. Обновите панель.");
                    if (selected.serverId && !servers.some(server => server.id === selected!.serverId)) {
                        throw new Error("Связанный сервер изменился. Закройте окно и обновите данные.");
                    }
                    name.value = selected.name;
                    endpoint.value = selected.endpointUrl;
                    description.value = selected.description ?? "";
                    enabled.checked = selected.isMonitoringEnabled;
                    parent.value = selected.serverId ?? "";
                    remove.hidden = false;
                }
                showType();
                loaded = true;
                setBusy(false);
                name.focus();
            }
            catch (failure: unknown) { showError(failure); }
            finally { if (version === openVersion) setBusy(false); }
        }
    };
}
