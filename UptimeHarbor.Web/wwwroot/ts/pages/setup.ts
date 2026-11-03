import { sendJsonRequest, unwrapServiceResult, unwrapServiceSuccess, type ServiceResult, type ServiceResultWithData } from "../shared/http.js";

interface SetupStatus {
    phase: string;
    mode: string;
    revision: number;
    generation: number;
    attemptId: string | null;
    reason: string | null;
    commonAccessOpen: boolean;
}

let status: SetupStatus | null = null;
let busy = false;

document.addEventListener("DOMContentLoaded", () => {
    void initSetup();
});

function required<T extends Element>(selector: string): T {
    const element = document.querySelector<T>(selector);
    if (element === null) {
        throw new Error(`Не найден элемент ${selector}.`);
    }
    return element;
}

async function initSetup(): Promise<void> {
    required<HTMLFormElement>("[data-setup-login]").addEventListener("submit", event => {
        event.preventDefault();
        void command(login);
    });
    required<HTMLFormElement>("[data-setup-prepare]").addEventListener("submit", event => {
        event.preventDefault();
        void command(prepare);
    });
    required<HTMLFormElement>("[data-setup-logout]").addEventListener("submit", event => {
        event.preventDefault();
        void command(logout);
    });
    required<HTMLSelectElement>("[data-setup-mode]").addEventListener("change", showCommonFields);
    showCommonFields();
    await command(refreshSession);
}

async function command(action: () => Promise<void>): Promise<void> {
    if (busy) {
        return;
    }
    busy = true;
    document.querySelectorAll<HTMLButtonElement>("button").forEach(button => { button.disabled = true; });
    try {
        await action();
    }
    catch (error) {
        required<HTMLElement>("[data-setup-message]").textContent = error instanceof Error ? error.message : "Настройка недоступна.";
    }
    finally {
        busy = false;
        document.querySelectorAll<HTMLButtonElement>("button").forEach(button => { button.disabled = false; });
    }
}

async function refreshSession(): Promise<void> {
    const session = unwrapServiceResult(await sendJsonRequest<ServiceResultWithData<{ token: string; authenticated: boolean }>>("/api/setup/session"));
    required<HTMLMetaElement>('meta[name="request-verification-token"]').content = session.token;
    required<HTMLElement>("[data-setup-login]").classList.toggle("d-none", session.authenticated);
    required<HTMLElement>("[data-setup-content]").classList.toggle("d-none", !session.authenticated);
    required<HTMLElement>("[data-setup-message]").textContent = "Рабочий доступ закрыт до завершения подключения.";
    if (session.authenticated) {
        status = unwrapServiceResult(await sendJsonRequest<ServiceResultWithData<SetupStatus>>("/api/setup/status"));
        required<HTMLElement>("[data-setup-status]").textContent = status.attemptId === null
            ? "Установка готова к локальной подготовке."
            : "Попытка сохранена. Установление доверия ещё недоступно; общий доступ закрыт.";
        required<HTMLElement>("[data-setup-prepare]").classList.toggle("d-none", status.attemptId !== null);
    }
}

async function login(): Promise<void> {
    const form = required<HTMLFormElement>("[data-setup-login]");
    const data = new FormData(form);
    try {
        unwrapServiceSuccess(await sendJsonRequest<ServiceResult>("/api/setup/login", "POST", {
            login: data.get("login"), password: data.get("password")
        }));
    }
    finally {
        required<HTMLInputElement>("#setup-password").value = "";
    }
    await refreshSession();
}

function showCommonFields(): void {
    const first = required<HTMLSelectElement>("[data-setup-mode]").value === "Bootstrap";
    const fields = required<HTMLFieldSetElement>("[data-setup-common]");
    fields.classList.toggle("d-none", !first);
    fields.disabled = !first;
    fields.querySelectorAll<HTMLInputElement>("input").forEach(input => { input.required = first; });
}

async function prepare(): Promise<void> {
    if (status === null) {
        throw new Error("Состояние установки не загружено.");
    }
    const form = required<HTMLFormElement>("[data-setup-prepare]");
    const data = new FormData(form);
    const first = data.get("mode") === "Bootstrap";
    try {
        status = unwrapServiceResult(await sendJsonRequest<ServiceResultWithData<SetupStatus>>("/api/setup/prepare", "POST", {
            mode: data.get("mode"), expectedRevision: status.revision, expectedGeneration: status.generation,
            commonLogin: first ? data.get("commonLogin") : null,
            commonPassword: first ? data.get("commonPassword") : null
        }));
    }
    finally {
        required<HTMLInputElement>("#setup-common-password").value = "";
    }
    await refreshSession();
}

async function logout(): Promise<void> {
    unwrapServiceSuccess(await sendJsonRequest<ServiceResult>("/api/setup/logout", "POST"));
    status = null;
    await refreshSession();
}
