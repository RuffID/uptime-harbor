import assert from "node:assert/strict";
import { test } from "node:test";
import { selectMonitoringObjects } from "../../UptimeHarbor.Web/wwwroot/ts/pages/monitoringSnapshot.ts";
import type { DashboardSnapshotDto } from "../../UptimeHarbor.Web/wwwroot/ts/pages/monitoringSnapshot.ts";

function snapshot(): DashboardSnapshotDto {
    const state = { description: null, lastCheckedAtUtc: null, startedAtUtc: null, uptimeSeconds: null };
    return {
        generatedAtUtc: "2026-11-01T00:00:00Z",
        servers: [{ ...state, id: "host", name: "Host", internalName: "host.internal", status: 2,
            containers: [{ ...state, id: "linked", serverId: "host", displayName: "Orders", internalName: "orders-process", status: 1 }] }],
        unassignedContainers: [{ ...state, id: "standalone", serverId: null, displayName: "Billing", internalName: null, status: 0 }]
    };
}

test("one list contains servers and linked and standalone applications", () => {
    const rows = selectMonitoringObjects(snapshot(), "", "");
    assert.deepEqual(rows.map(row => [row.id, row.type, row.serverName]), [
        ["host", "Server", "—"], ["linked", "Application", "Host"], ["standalone", "Application", "Без сервера"]
    ]);
    assert.equal(rows[1].name, "Orders");
    assert.equal(rows[1].internalName, "orders-process");
});

test("type and status filters combine without hiding an application because its server is unavailable", () => {
    assert.deepEqual(selectMonitoringObjects(snapshot(), "Application", "1").map(row => row.id), ["linked"]);
    assert.deepEqual(selectMonitoringObjects(snapshot(), "Server", "").map(row => row.id), ["host"]);
    assert.deepEqual(selectMonitoringObjects(snapshot(), "Application", "0").map(row => row.id), ["standalone"]);
    assert.deepEqual(selectMonitoringObjects(snapshot(), "Server", "1"), []);
    assert.throws(() => selectMonitoringObjects(snapshot(), "Unexpected", ""), /Неизвестный тип/);
});
