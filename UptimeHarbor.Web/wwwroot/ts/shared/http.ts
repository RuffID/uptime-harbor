export interface ServiceResult {
    success: boolean;
    message?: string;
    field?: string | null;
}

export interface ServiceResultWithData<TData> extends ServiceResult {
    data?: TData;
}

export class ApiError extends Error {
    public constructor(
        message: string,
        public readonly field: string | null,
        public readonly status: number | null = null
    ) {
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
    body: object | null = null,
    signal: AbortSignal | null = null
): Promise<TResponse> {
    const headers: Record<string, string> = { Accept: "application/json" };
    const options: RequestInit = { method, headers };

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
        return JSON.parse(text) as TResponse;
    }
    catch {
        throw new Error("Сервер вернул некорректный JSON-ответ.");
    }
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

export function isAbortError(error: unknown): boolean {
    return error instanceof Error && error.name === "AbortError";
}

function createResponseError(text: string, status: number): ApiError {
    if (!text.trim()) {
        return new ApiError(`Ошибка HTTP ${status}.`, null, status);
    }

    let parsed: unknown;
    try {
        parsed = JSON.parse(text) as unknown;
    }
    catch {
        return new ApiError(
            `Сервер вернул ошибку HTTP ${status} в неподдерживаемом формате.`,
            null,
            status);
    }

    if (!isServiceErrorEnvelope(parsed)) {
        return new ApiError(
            `Сервер вернул некорректный ответ об ошибке (HTTP ${status}).`,
            null,
            status);
    }

    return new ApiError(parsed.message, parsed.field ?? null, status);
}

function isServiceErrorEnvelope(value: unknown): value is ServiceResult & { message: string } {
    if (typeof value !== "object" || value === null) {
        return false;
    }

    const candidate = value as Partial<ServiceResult>;
    return candidate.success === false
        && typeof candidate.message === "string"
        && candidate.message.trim().length > 0
        && (candidate.field === undefined
            || candidate.field === null
            || typeof candidate.field === "string");
}
