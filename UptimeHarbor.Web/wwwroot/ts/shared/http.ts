export interface ServiceResult {
    success: boolean;
    message?: string;
    field?: string | null;
}

export interface ServiceResultWithData<TData> extends ServiceResult {
    data?: TData;
}

export class ApiError extends Error {
    public constructor(message: string, public readonly field: string | null) {
        super(message);
        this.name = "ApiError";
    }
}

export function getRequestVerificationToken(): string {
    const element = document.querySelector<HTMLMetaElement>('meta[name="request-verification-token"]');
    if (!element?.content) {
        throw new Error("Antiforgery token не найден.");
    }

    return element.content;
}

export async function sendJsonRequest<TResponse>(
    url: string,
    method = "GET",
    body: object | null = null
): Promise<TResponse> {
    const headers: Record<string, string> = { Accept: "application/json" };
    const options: RequestInit = { method, headers };

    if (body !== null) {
        headers["Content-Type"] = "application/json";
        options.body = JSON.stringify(body);
    }

    if (method !== "GET") {
        headers.RequestVerificationToken = getRequestVerificationToken();
    }

    const response = await fetch(url, options);
    const text = await response.text();
    const parsed = text ? JSON.parse(text) as ServiceResult : null;

    if (!response.ok) {
        throw new ApiError(
            parsed?.message ?? `HTTP error ${response.status}`,
            parsed?.field ?? null);
    }

    if (parsed === null) {
        throw new Error("Сервер вернул пустой JSON-ответ.");
    }

    return parsed as TResponse;
}

export function unwrapServiceResult<TData>(result: ServiceResultWithData<TData>): TData {
    if (!result.success || result.data === undefined) {
        throw new ApiError(result.message ?? "Ошибка операции.", result.field ?? null);
    }

    return result.data;
}

export function unwrapServiceSuccess(result: ServiceResult): void {
    if (!result.success) {
        throw new ApiError(result.message ?? "Ошибка операции.", result.field ?? null);
    }
}
