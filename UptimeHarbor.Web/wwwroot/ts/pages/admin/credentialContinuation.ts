import { ApiError } from "../../shared/http.js";
import { clusterApi, type CredentialContinuationStatus } from "./clusterApi.js";
import { LatestRequest } from "./clusterDraft.js";

export function initializeCredentialContinuation(root: HTMLElement): { reset(): void; close(): void } | null {
    const section = root.querySelector<HTMLElement>("[data-credential-continuation]");
    if (!section) return null;

    function require<T extends HTMLElement>(selector: string): T {
        const element = section!.querySelector<T>(selector);
        if (!element) throw new Error(`Отсутствует элемент ${selector}.`);
        return element;
    }

    const lookup = require<HTMLFormElement>("[data-continuation-lookup]");
    const requestId = require<HTMLInputElement>("#continuationRequestId");
    const open = require<HTMLButtonElement>("[data-continuation-lookup] button");
    const form = require<HTMLFormElement>("[data-continuation-form]");
    const fields = require<HTMLFieldSetElement>("[data-continuation-form] fieldset");
    const password = require<HTMLInputElement>("#continuationPassword");
    const status = require<HTMLElement>("[data-continuation-status]");
    const error = require<HTMLElement>("[data-continuation-error]");
    const latest = new LatestRequest();
    let selected: CredentialContinuationStatus | null = null;
    let busy = false;
    let closed = false;

    function setBusy(value: boolean): void {
        busy = value;
        requestId.disabled = value || closed;
        open.disabled = value || closed;
        fields.disabled = value || closed || selected === null || selected.confirmationId !== null;
    }

    function reset(): void {
        latest.invalidate();
        selected = null;
        password.value = "";
        status.textContent = "";
        error.textContent = "";
        form.hidden = true;
        setBusy(false);
    }

    function show(value: CredentialContinuationStatus): void {
        selected = value;
        form.hidden = value.confirmationId !== null;
        const target = `Запрос ${value.requestId}, узел ${value.nodeId}.`;
        status.textContent = value.confirmationId
            ? `${target} Подтверждение принято. ID подтверждения: ${value.confirmationId}.`
            : `${target} Ожидает подтверждения администратора.`;
    }

    function failed(value: unknown, fallback: string): void {
        error.textContent = value instanceof ApiError ? value.message : fallback;
    }

    async function openRequest(): Promise<void> {
        reset();
        const id = requestId.value.trim();
        if (!/^[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}$/i.test(id)) {
            error.textContent = "Введите ID операции в формате UUID.";
            return;
        }

        const request = latest.begin();
        setBusy(true);
        try {
            const result = await clusterApi.continuation(id, request.signal);
            if (latest.isCurrent(request.version)) show(result);
        }
        catch (value: unknown) {
            if (latest.isCurrent(request.version)) failed(value, "Не удалось загрузить запрос.");
        }
        finally {
            if (latest.isCurrent(request.version)) setBusy(false);
        }
    }

    async function confirm(value: CredentialContinuationStatus): Promise<void> {
        const request = latest.begin();
        setBusy(true);
        error.textContent = "";
        try {
            const pending = clusterApi.confirmContinuation(value.requestId, value.requestDigest, password.value);
            password.value = "";
            const confirmationId = await pending;
            if (latest.isCurrent(request.version)) show({ ...value, confirmationId });
        }
        catch (failure: unknown) {
            if (latest.isCurrent(request.version)) {
                if (!(failure instanceof ApiError && failure.status === 400)) reset();
                failed(failure, "Ответ не получен. Откройте тот же запрос, чтобы проверить результат.");
            }
        }
        finally {
            if (latest.isCurrent(request.version)) setBusy(false);
        }
    }

    requestId.addEventListener("input", reset);
    lookup.addEventListener("submit", event => {
        event.preventDefault();
        if (!busy && !closed) void openRequest();
    });
    form.addEventListener("submit", event => {
        event.preventDefault();
        if (!busy && !closed && selected && !selected.confirmationId) void confirm(selected);
    });
    setBusy(false);
    return {
        reset,
        close(): void {
            closed = true;
            reset();
        }
    };
}
