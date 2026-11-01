export type EntityKind = "Server" | "Container" | "Chat" | "Binding" | "NodeChatRoute" | "Account";
export interface Settings {
    name: string | null; endpoint: string | null; description: string | null; enabled: boolean;
    parentId: string | null; chatId: string | null; telegramChatId: number;
    observerNodes: string[]; failureThreshold: number; intervalSeconds: number; timeoutSeconds: number;
}
export interface Entity { id: string; kind: EntityKind; revision: number; deleted: boolean; settings: Settings; }
export interface Operation {
    operationId: string; status: string; appliedRevision: number | null; conflictId: string | null;
    replacedBy: string | null; message: string | null; replacementResult: Operation | null;
}
export interface Pending {
    operationId: string; entityId: string; kind: EntityKind | "Node"; action: string; baseRevision: number;
    state: string; createdAtUtc: string; settings: Settings | null; requestedControlId: string | null;
    replacementOperationId: string | null; result: Operation | null; accountAction: string | null;
}
export interface Conflict {
    id: string; entityId: string; kind: EntityKind; action: string; accountAction: string | null;
    baseRevision: number; currentRevision: number; baseline: Entity | null; firstAccepted: Entity | null;
    proposed: Settings | null; current: Entity | null; resolutionOperationId: string | null; resolvedAtUtc: string | null;
}
export interface Observer {
    nodeId: string; status: string; checkedAtUtc: string | null; failure: string | null;
    endpointName: string | null; startedAtUtc: string | null; metadataAtUtc: string | null; uptimeSeconds: number | null;
}
export interface Target {
    id: string; availability: string; coverage: string; evaluatedAtUtc: string | null;
    evaluationStale: boolean; observers: Observer[];
}
export interface Delivery {
    id: string; chatId: string; incidentId: string | null; kind: string; status: string;
    attempts: number; failure: string | null; completedAtUtc: string | null;
}
export interface Snapshot {
    nodeId: string; currentUserId: string; revision: number; atUtc: string; entities: Entity[];
    nodes: string[]; conflicts: Conflict[]; targets: Target[]; deliveries: Delivery[];
}
export interface NodeDiagnostic {
    nodeId: string; connection: string; ageSeconds: number | null; lastContactAtUtc: string | null;
    database: string; role: string; appliedIndex: number | null; lag: number | null;
}
export interface Diagnostic {
    term: number | null; membershipVersion: number | null; operationsRevision: number; maintenanceId: string;
    conflicts: number; terminalDeliveryErrors: number; lastSuccessfulSynchronizationAtUtc: string | null;
    projectionAgeSeconds: number | null;
    nodeId: string; leaderNodeId: string | null; role: string; majority: string; votingNodes: number;
    requiredMajority: number; appliedIndex: number; committedIndex: number | null; lag: number | null;
    pending: number; heldPending: number; deliveryBacklog: number; localDatabase: string;
    telegram: string; telegramReason: string; telegramAgeSeconds: number | null; atUtc: string; nodes: NodeDiagnostic[];
}
export interface LifecycleChange {
    operationId: string; action: "Cancel"; nodeId: string; baseRevision: number;
    membershipVersion: number; targetOperationId: string;
}
export interface Change {
    operationId: string; entityId: string; kind: EntityKind; baseRevision: number; action: string;
    settings: Settings | null; resolvesConflictId: string | null; targetOperationId: string | null;
    controlOperationId: string | null; confirmRestored: boolean; confirmGlobalDelete: boolean;
}
export interface AccountChange {
    operationId: string; userId: string; baseRevision: number; action: string; login: string | null;
    password: string | null; isEnabled: boolean | null; resolvesConflictId: string | null;
    targetOperationId: string | null; controlOperationId: string | null; confirmRestored: boolean;
}
export interface AccountOperation { operationId: string; status: string; outcome: Operation; }
