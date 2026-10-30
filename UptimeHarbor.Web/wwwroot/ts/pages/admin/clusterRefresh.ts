import { LatestRequest } from "./clusterDraft.js";
import type { Snapshot, Pending, Diagnostic } from "./clusterModels.js";

interface AdministrationReader {
    snapshot(signal: AbortSignal): Promise<Snapshot>;
    pending(offset: number, signal: AbortSignal): Promise<Pending[]>;
    diagnostics(signal: AbortSignal): Promise<Diagnostic>;
}

/** Каждый reconnect/refresh проходит защищённое API заново; поздние ответы исключаются целиком. */
export class AdministrationRefresh {
    private latest = new LatestRequest();
    private api: AdministrationReader;
    constructor(api: AdministrationReader) { this.api = api; }

    invalidate(): void { this.latest.invalidate(); }

    async read(offset: number): Promise<[Snapshot, Pending[], Diagnostic] | null> {
        const request = this.latest.begin();
        try {
            const result = await Promise.all([
                this.api.snapshot(request.signal), this.api.pending(offset, request.signal), this.api.diagnostics(request.signal)
            ]);
            if (!this.latest.isCurrent(request.version)) return null;
            return result;
        }
        catch (error: unknown) {
            if (!this.latest.isCurrent(request.version)) return null;
            throw error;
        }
    }
}
