export class ApiError extends Error {
    field;
    constructor(message, field) {
        super(message);
        this.field = field;
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
export async function sendJsonRequest(url, method = "GET", body = null) {
    const headers = { Accept: "application/json" };
    const options = { method, headers };
    if (body !== null) {
        headers["Content-Type"] = "application/json";
        options.body = JSON.stringify(body);
    }
    if (method !== "GET") {
        headers.RequestVerificationToken = getRequestVerificationToken();
    }
    const response = await fetch(url, options);
    const text = await response.text();
    const parsed = text ? JSON.parse(text) : null;
    if (!response.ok) {
        throw new ApiError(parsed?.message ?? `HTTP error ${response.status}`, parsed?.field ?? null);
    }
    if (parsed === null) {
        throw new Error("Сервер вернул пустой JSON-ответ.");
    }
    return parsed;
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
