export type ServerType = "Server" | "Application";
interface MonitoringState {
    id: string; description: string | null; status: number;
    lastCheckedAtUtc: string | null; startedAtUtc: string | null; uptimeSeconds: number | null;
}
export interface DashboardContainerDto extends MonitoringState {
    serverId: string | null; displayName: string; internalName: string | null;
}
export interface DashboardServerDto extends MonitoringState {
    name: string; internalName: string | null; containers: DashboardContainerDto[];
}
export interface DashboardSnapshotDto {
    generatedAtUtc: string; servers: DashboardServerDto[]; unassignedContainers: DashboardContainerDto[];
}
export interface MonitoringRow extends MonitoringState {
    type: ServerType; name: string; internalName: string | null; serverName: string;
}

export function selectMonitoringObjects(snapshot: DashboardSnapshotDto, type: string, status: string): MonitoringRow[] {
    if (type !== "" && type !== "Server" && type !== "Application") throw new Error("Неизвестный тип объекта.");
    const rows: MonitoringRow[] = [];
    for (const server of snapshot.servers) {
        rows.push({ ...server, type: "Server", serverName: "—" });
        for (const application of server.containers) {
            rows.push({ ...application, type: "Application", name: application.displayName, serverName: server.name });
        }
    }
    for (const application of snapshot.unassignedContainers) {
        rows.push({ ...application, type: "Application", name: application.displayName, serverName: "Без сервера" });
    }
    return rows.filter(row => (type === "" || row.type === type) && (status === "" || String(row.status) === status));
}
