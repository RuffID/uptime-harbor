import assert from "node:assert/strict";
import { beforeEach, test } from "node:test";
import { registerHooks } from "node:module";
import { CommandDraft, LatestRequest } from "../../UptimeHarbor.Web/wwwroot/ts/pages/admin/clusterDraft.ts";
import type { Snapshot, Entity, Change, AccountChange, Operation, Diagnostic } from "../../UptimeHarbor.Web/wwwroot/ts/pages/admin/clusterModels.ts";

// Зависимости production TS используют браузерные .js specifiers; здесь разрешаются к тем же исходникам.
registerHooks({ resolve(specifier, context, nextResolve) {
    if (context.parentURL?.includes("/wwwroot/ts/pages/admin/") && specifier.startsWith("./") && specifier.endsWith(".js")) {
        return nextResolve(specifier.slice(0, -3) + ".ts", context);
    }
    return nextResolve(specifier, context);
} });
const { ClusterEditor } = await import("../../UptimeHarbor.Web/wwwroot/ts/pages/admin/clusterEditor.ts");
const { delegateActions, operationText } = await import("../../UptimeHarbor.Web/wwwroot/ts/pages/admin/clusterDom.ts");
const { AdministrationRefresh } = await import("../../UptimeHarbor.Web/wwwroot/ts/pages/admin/clusterRefresh.ts");

class Dom {
    tag: string;
    children: Dom[] = [];
    parent: Dom | null = null;
    dataset: Record<string, string> = {};
    listeners = new Map<string, ((event: Event) => void)[]>();
    ownText = "";
    inputValue: string | null = null;
    name = ""; type = ""; id = ""; htmlFor = ""; className = ""; autocomplete = ""; min = "";
    checked = false; selected = false; disabled = false; required = false; multiple = false; maxLength = 0;
    constructor(tag: string) { this.tag = tag; }
    set textContent(value: string) { this.ownText = value; this.children = []; }
    get textContent(): string { return this.ownText + this.children.map(child => child.textContent).join(""); }
    get options(): Dom[] { return this.children.filter(child => child.tag === "option"); }
    get selectedOptions(): Dom[] { return this.options.filter(option => option.selected); }
    set value(value: string) { this.inputValue = value; }
    get value(): string { return this.inputValue ?? (this.tag === "select" ? this.options.find(option => option.selected)?.value ?? this.options[0]?.value : "") ?? ""; }
    append(...nodes: Dom[]): void { for (const node of nodes) { node.parent = this; this.children.push(node); } }
    prepend(...nodes: Dom[]): void { for (const node of nodes) node.parent = this; this.children.unshift(...nodes); }
    replaceChildren(...nodes: Dom[]): void { this.children = []; this.append(...nodes); }
    descendants(): Dom[] { return this.children.flatMap(child => [child, ...child.descendants()]); }
    contains(node: Dom): boolean { return node === this || this.descendants().includes(node); }
    closest(selector: string): Dom | null {
        if (selector === "button[data-cluster-action]" && this.tag === "button" && this.dataset.clusterAction) return this;
        return this.parent?.closest(selector) ?? null;
    }
    setAttribute(): void { }
    focus(): void { }
    reset(): void { for (const node of this.descendants()) if (node.tag === "input") node.value = ""; }
    reportValidity(): boolean { return !this.descendants().some(node => node.required && node.type === "checkbox" && !node.checked); }
    addEventListener(type: string, listener: (event: Event) => void): void {
        this.listeners.set(type, [...(this.listeners.get(type) ?? []), listener]);
    }
    dispatchEvent(event: Event): boolean {
        if (!event.target) Object.defineProperty(event, "target", { value: this });
        for (const listener of this.listeners.get(event.type) ?? []) listener(event);
        if (event.bubbles) this.parent?.dispatchEvent(event);
        return !event.defaultPrevented;
    }
}
beforeEach(() => {
    Object.assign(globalThis, { Element: Dom, document: {
        createElement: (tag: string) => new Dom(tag), createTextNode: (text: string) => Object.assign(new Dom("text"), { ownText: text })
    }, window: { confirm: () => true }, localStorage: { setItem: () => { throw new Error("Secrets must stay in memory"); } } });
});
const flush = () => new Promise<void>(resolve => setImmediate(resolve));
function entity(kind = "Server"): Entity {
    return { id: "entity-id", kind: kind as Entity["kind"], revision: 7, deleted: false, settings: {
        name: "accepted", endpoint: "https://test.invalid/health", description: null, enabled: true,
        parentId: null, chatId: null, telegramChatId: 123, observerNodes: [], failureThreshold: 3, intervalSeconds: 60, timeoutSeconds: 10
    } };
}
function snapshot(value: Entity): Snapshot {
    return { nodeId: "local", currentUserId: "actor", revision: value.revision, atUtc: "2026-10-29T00:00:00Z",
        entities: [value], nodes: [], conflicts: [], targets: [], deliveries: [] };
}
function operation(status = "LocalPending"): Operation {
    return { operationId: "operation", status, appliedRevision: null, conflictId: null, replacedBy: null, message: null, replacementResult: null };
}
function editorFixture(value = entity()) {
    const host = new Dom("section"); delegateActions(host as unknown as HTMLElement);
    let accepted = snapshot(value);
    let response: () => Promise<Operation> = async () => operation();
    const sent: (Change | AccountChange)[] = [];
    const errors: unknown[] = [];
    const editor = new ClusterEditor(host as unknown as HTMLElement, {
        snapshot: () => accepted, refresh: async () => { }, failure: error => errors.push(error),
        show: () => { }, hide: () => { },
        submit: async body => { sent.push(structuredClone(body)); return await response(); }
    });
    return { host, editor, sent, errors, setResponse: (next: typeof response) => { response = next; },
        setAccepted: (next: Snapshot) => { accepted = next; },
        input: (name: string): Dom => { const input = host.descendants().find(node => node.name === name); assert.ok(input); return input; },
        submit: async () => { host.descendants().find(node => node.tag === "form")!.dispatchEvent(new Event("submit", { cancelable: true })); await flush(); },
        click: async (text: string) => { const button = host.descendants().find(node => node.tag === "button" && node.textContent === text); assert.ok(button); button.dispatchEvent(new Event("click", { bubbles: true })); await flush(); }
    };
}

test("draft freezes the original request; network retry cannot reuse ID for altered input", () => {
    const draft = new CommandDraft<{ id: string; name: string }>();
    const request = { id: "stable", name: "typed" };
    assert.deepEqual(draft.prepare(request), request);
    const retry = draft.prepare(request); retry.name = "mutated response";
    assert.equal(draft.prepare(request).name, "typed");
    assert.throws(() => draft.prepare({ ...request, name: "new" }), /OperationId/);
});

test("editor keeps input and OperationId after lost response; explicit rebase preserves draft", async () => {
    const fixture = editorFixture(); fixture.editor.open("Server", entity());
    fixture.input("name").value = "unsaved input";
    fixture.setResponse(async () => { throw new TypeError("Network failure"); });
    await fixture.submit();
    assert.equal(fixture.input("name").value, "unsaved input");
    assert.match(fixture.host.textContent, /Ввод сохранён/);
    fixture.setResponse(async () => operation());
    await fixture.submit();
    assert.deepEqual(fixture.sent[1], fixture.sent[0]);
    fixture.input("name").value = "correction";
    await fixture.submit();
    assert.equal(fixture.sent.length, 2);
    fixture.setAccepted(snapshot({ ...entity(), revision: 11 }));
    await fixture.click("Новая команда от актуальной ревизии");
    await fixture.submit();
    assert.equal(fixture.sent[2].baseRevision, 11);
    assert.notEqual(fixture.sent[2].operationId, fixture.sent[0].operationId);
    assert.equal((fixture.sent[2] as Change).settings!.name, "correction");
});

test("repeated conflict retains proposal; new solution uses current base without rollback", async () => {
    const fixture = editorFixture();
    const conflict = { id: "conflict", entityId: "entity-id", kind: "Server" as const, action: "Upsert", accountAction: null,
        baseRevision: 2, currentRevision: 7, baseline: null, firstAccepted: entity(), proposed: { ...entity().settings, name: "proposal" },
        current: entity(), resolutionOperationId: null, resolvedAtUtc: null };
    fixture.editor.open("Server", entity(), "Upsert", null, conflict);
    fixture.setResponse(async () => operation("Conflict"));
    await fixture.submit();
    assert.equal(fixture.input("name").value, "proposal");
    assert.match(fixture.host.textContent, /Первая принятая версия/);
    fixture.setAccepted(snapshot({ ...entity(), revision: 12 }));
    await fixture.click("Новая команда от актуальной ревизии");
    await fixture.submit();
    assert.equal(fixture.sent[1].baseRevision, 12);
    assert.equal(fixture.sent[1].resolvesConflictId, "conflict");
    assert.equal((fixture.sent[1] as Change).settings!.name, "proposal");
});

test("held Pending needs explicit confirmation and new control; original payload stays held", async () => {
    const fixture = editorFixture();
    const held = { operationId: "held", entityId: "entity-id", kind: "Server" as const, action: "Upsert", baseRevision: 2,
        state: "NeedsUserConfirmation", createdAtUtc: "2026-10-29T00:00:00Z", settings: entity().settings,
        requestedControlId: null, replacementOperationId: null, result: null, accountAction: null };
    fixture.editor.open("Server", entity(), "Upsert", held);
    await fixture.submit(); assert.equal(fixture.sent.length, 0);
    fixture.input("restored").checked = true;
    await fixture.submit();
    const command = fixture.sent[0] as Change;
    assert.equal(command.targetOperationId, "held");
    assert.equal(command.confirmRestored, true);
    assert.equal(command.baseRevision, 7);
    assert.notEqual(command.controlOperationId, command.operationId);
    assert.notEqual(command.operationId, "held");
    await fixture.click("Новая команда от актуальной ревизии");
    assert.equal(fixture.input("restored").checked, false);
});

test("account password stays in memory and survives failure; close clears editor", async () => {
    const fixture = editorFixture(entity("Account"));
    fixture.editor.open("Account", entity("Account"), "Password");
    fixture.input("password").value = "Private-new-password!";
    fixture.setResponse(async () => { throw new Error("Network failure"); });
    await fixture.submit();
    assert.equal(fixture.input("password").value, "Private-new-password!");
    assert.equal((fixture.sent[0] as AccountChange).password, "Private-new-password!");
    assert.doesNotMatch(fixture.host.textContent, /Private-new-password/);
    fixture.editor.close(); assert.equal(fixture.host.children.length, 0);
});

test("test-send command uses base zero and never reports Telegram delivery success", async () => {
    const fixture = editorFixture(entity("Chat"));
    fixture.editor.open("Chat", entity("Chat"), "TestDelivery");
    fixture.setResponse(async () => operation("Committed"));
    await fixture.submit();
    assert.equal(fixture.sent[0].baseRevision, 0);
    assert.equal(fixture.sent[0].action, "TestDelivery");
    assert.match(fixture.host.textContent, /не подтверждение доставки Telegram/);
    assert.doesNotMatch(operationText(operation()), /успешно доставлено/i);
});

test("latest request aborts old work and invalidation prevents late protected response", () => {
    const latest = new LatestRequest(); const first = latest.begin(); const second = latest.begin();
    assert.equal(first.signal.aborted, true);
    assert.equal(latest.isCurrent(first.version), false);
    assert.equal(latest.isCurrent(second.version), true);
    latest.invalidate(); assert.equal(latest.isCurrent(second.version), false);
});

test("refresh/reconnect reads accepted, Pending and diagnostics again; stale replies are discarded", async () => {
    let resolveOld!: (value: Snapshot) => void;
    let reads = 0; let pendings = 0; let diagnostics = 0;
    const refresher = new AdministrationRefresh({
        snapshot: async () => { reads++; if (reads === 1) return await new Promise<Snapshot>(resolve => { resolveOld = resolve; }); return snapshot({ ...entity(), revision: 9 }); },
        pending: async () => { pendings++; return []; }, diagnostics: async () => { diagnostics++; return {} as Diagnostic; }
    });
    const old = refresher.read(0);
    const reconnected = await refresher.read(0);
    assert.equal(reconnected![0].revision, 9);
    resolveOld(snapshot(entity()));
    assert.equal(await old, null);
    assert.equal(reads, 2); assert.equal(pendings, 2); assert.equal(diagnostics, 2);
});

test("current refresh authorization failure propagates and cannot become accepted success", async () => {
    const forbidden = Object.assign(new Error("Account disabled"), { status: 403 });
    const refresher = new AdministrationRefresh({ snapshot: async () => { throw forbidden; },
        pending: async () => [], diagnostics: async () => ({} as Diagnostic) });
    await assert.rejects(refresher.read(0), error => error === forbidden);
});

test("compiled API uses common antiforgery HTTP helper; HTTP 202 remains queued and 403 propagates", async () => {
    // Штатная Web-сборка создаёт эти модули; тест не компилирует/запускает приложение.
    const { clusterApi } = await import("../../UptimeHarbor.Web/wwwroot/dist/pages/admin/clusterApi.js");
    const calls: { url: string; options: RequestInit }[] = [];
    Object.assign(globalThis, { document: { querySelector: () => ({ content: "test-antiforgery" }) },
        fetch: async (url: string, options: RequestInit) => {
            calls.push({ url, options });
            return new Response(JSON.stringify({ success: true, data: operation() }), { status: 202 });
        } });
    const body: Change = { operationId: "stable-operation", entityId: "chat", kind: "Chat", baseRevision: 0,
        action: "TestDelivery", settings: null, resolvesConflictId: null, targetOperationId: null,
        controlOperationId: null, confirmRestored: false, confirmGlobalDelete: false };
    const result = await clusterApi.change(body);
    assert.equal(result.status, "LocalPending");
    assert.match(operationText(result), /Ожидает синхронизации/);
    assert.equal((calls[0].options.headers as Record<string, string>).RequestVerificationToken, "test-antiforgery");
    assert.equal(JSON.parse(calls[0].options.body as string).operationId, "stable-operation");
    Object.assign(globalThis, { fetch: async () => new Response(JSON.stringify({ success: false, message: "Account disabled" }), { status: 403 }) });
    await assert.rejects(clusterApi.change(body), error => (error as { status: number }).status === 403);
});
