export class ApiError extends Error {
    field;
    status;
    constructor(message, field, status = null) {
        super(message);
        this.field = field;
        this.status = status;
        this.name = "ApiError";
    }
}
export function getRequestVerificationToken() {
    const element = document.querySelector('meta[name="request-verification-token"]');
    if (!element?.content) {
        throw new Error("Antiforgery token не найден.");
    }
    return element.content;
}
export async function sendJsonRequest(url, method = "GET", body = null, signal = null) {
    const headers = { Accept: "application/json" };
    const options = { method, headers };
    if (signal !== null) {
        options.signal = signal;
    }
    if (body !== null) {
        headers["Content-Type"] = "application/json";
        options.body = JSON.stringify(body);
    }
    if (method !== "GET") {
        headers.RequestVerificationToken = getRequestVerificationToken();
    }
    const response = await fetch(url, options);
    const text = await response.text();
    if (!response.ok) {
        throw createResponseError(text, response.status);
    }
    if (!text.trim()) {
        throw new Error("Сервер вернул пустой JSON-ответ.");
    }
    try {
        return JSON.parse(text);
    }
    catch {
        throw new Error("Сервер вернул некорректный JSON-ответ.");
    }
}
export function unwrapServiceResult(result) {
    if (!result.success || result.data === undefined) {
        throw new ApiError(result.message ?? "Ошибка операции.", result.field ?? null);
    }
    return result.data;
}
export function unwrapServiceSuccess(result) {
    if (!result.success) {
        throw new ApiError(result.message ?? "Ошибка операции.", result.field ?? null);
    }
}
export function isAbortError(error) {
    return error instanceof Error && error.name === "AbortError";
}
function createResponseError(text, status) {
    if (!text.trim()) {
        return new ApiError(`Ошибка HTTP ${status}.`, null, status);
    }
    let parsed;
    try {
        parsed = JSON.parse(text);
    }
    catch {
        return new ApiError(`Сервер вернул ошибку HTTP ${status} в неподдерживаемом формате.`, null, status);
    }
    if (!isServiceErrorEnvelope(parsed)) {
        return new ApiError(`Сервер вернул некорректный ответ об ошибке (HTTP ${status}).`, null, status);
    }
    return new ApiError(parsed.message, parsed.field ?? null, status);
}
function isServiceErrorEnvelope(value) {
    if (typeof value !== "object" || value === null) {
        return false;
    }
    const candidate = value;
    return candidate.success === false
        && typeof candidate.message === "string"
        && candidate.message.trim().length > 0
        && (candidate.field === undefined
            || candidate.field === null
            || typeof candidate.field === "string");
}
