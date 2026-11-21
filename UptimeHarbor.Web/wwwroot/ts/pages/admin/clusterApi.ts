import { sendJsonRequest, unwrapServiceResult } from "../../shared/http.js";
import type { ServiceResultWithData } from "../../shared/http.js";
import type { Snapshot, Pending, Diagnostic, Change, AccountChange, AccountOperation, Operation, LifecycleChange } from "./clusterModels.js";

async function read<T>(url: string, signal: AbortSignal): Promise<T> {
    return unwrapServiceResult(await sendJsonRequest<ServiceResultWithData<T>>(url, "GET", null, signal));
}
async function post<T>(url: string, body: object): Promise<T> {
    return unwrapServiceResult(await sendJsonRequest<ServiceResultWithData<T>>(url, "POST", body));
}
export const clusterApi = {
    snapshot: (signal: AbortSignal) => read<Snapshot>("/api/admin/cluster", signal),
    pending: (offset: number, signal: AbortSignal) => read<Pending[]>(`/api/admin/cluster/pending?offset=${offset}`, signal),
    diagnostics: (signal: AbortSignal) => read<Diagnostic>("/api/admin/cluster/diagnostics", signal),
    find: (id: string, signal: AbortSignal) => read<Operation>(`/api/admin/cluster/operations/${encodeURIComponent(id)}`, signal),
    change: (body: Change) => post<Operation>("/api/admin/cluster/commands", body),
    lifecycle: (body: LifecycleChange) => post<Operation>("/api/admin/cluster/lifecycle", body),
    account: async (body: AccountChange): Promise<Operation> => (await post<AccountOperation>("/api/admin/users/commands", body)).outcome,
    cancel: (target: string, id: string) => post<Operation>(`/api/admin/cluster/pending/${encodeURIComponent(target)}/cancel`, { operationId: id })
};
