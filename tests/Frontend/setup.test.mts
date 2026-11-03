import assert from "node:assert/strict";
import { test } from "node:test";
import { registerHooks } from "node:module";

registerHooks({ resolve(specifier, context, next) {
    if (context.parentURL?.endsWith("/pages/setup.ts") && specifier === "../shared/http.js") {
        return next(new URL("../../artifacts/installation-setup/typescript/shared/http.js", import.meta.url).href, context);
    }
    return next(specifier, context);
} });

class ElementStub {
    value = ""; content = ""; textContent = ""; disabled = false; required = false;
    hidden = new Set<string>();
    fields: Record<string, unknown> = {};
    events = new Map<string, (event: Event) => void>();
    inputs: ElementStub[] = [];
    classList = { toggle: (name: string, on: boolean) => { if (on) this.hidden.add(name); else this.hidden.delete(name); } };
    addEventListener(name: string, callback: (event: Event) => void) { this.events.set(name, callback); }
    querySelectorAll() { return this.inputs; }
    submit() { this.events.get("submit")!(new Event("submit", { cancelable: true })); }
}
let initialize: () => void;
let nodes: Map<string, ElementStub>;
const calls: { url: string; options: RequestInit }[] = [];
let respond: (url: string, options: RequestInit) => unknown;
Object.assign(globalThis, {
    document: {
        addEventListener: (_: string, callback: () => void) => { initialize = callback; },
        querySelector: (selector: string) => nodes.get(selector) ?? null,
        querySelectorAll: () => [nodes.get("button")!]
    },
    FormData: class {
        form: ElementStub;
        constructor(form: ElementStub) { this.form = form; }
        get(name: string) { return this.form.fields[name] ?? null; }
    },
    fetch: async (url: string, options: RequestInit) => {
        calls.push({ url, options });
        return new Response(JSON.stringify(await respond(url, options)), { status: 200 });
    },
    localStorage: { setItem: () => { throw new Error("Setup must not persist secrets in browser storage"); } }
});
await import("../../UptimeHarbor.Web/wwwroot/ts/pages/setup.ts");
const flush = () => new Promise<void>(resolve => setImmediate(resolve));
function fixture() {
    nodes = new Map([
        "[data-setup-login]", "[data-setup-prepare]", "[data-setup-logout]", "[data-setup-mode]",
        "[data-setup-common]", "[data-setup-message]", "[data-setup-content]", "[data-setup-status]",
        'meta[name="request-verification-token"]', "#setup-password", "#setup-common-password", "button"
    ].map(selector => [selector, new ElementStub()]));
    nodes.get("[data-setup-mode]")!.value = "Join";
    calls.length = 0;
}
const saved = { phase: "LocalPrepared", mode: "Join", revision: 4, generation: 2,
    attemptId: "same-attempt", reason: null, commonAccessOpen: false };

test("login refreshes authenticated CSRF token, clears password and never opens common UI", async () => {
    // Arrange
    fixture();
    let authenticated = false;
    respond = (url, options) => {
        if (url.endsWith("/login")) {
            assert.equal((options.headers as Record<string, string>).RequestVerificationToken, "anonymous-token");
            authenticated = true;
            return { success: true };
        }
        return { success: true, data: url.endsWith("/session")
            ? { authenticated, token: authenticated ? "setup-token" : "anonymous-token" } : saved };
    };
    initialize!(); await flush();
    nodes.get("[data-setup-login]")!.fields = { login: "local", password: "secret-password" };
    nodes.get("#setup-password")!.value = "secret-password";

    // Act
    nodes.get("[data-setup-login]")!.submit(); await flush();

    // Assert
    assert.equal(nodes.get("#setup-password")!.value, "");
    assert.equal(nodes.get('meta[name="request-verification-token"]')!.content, "setup-token");
    assert.equal(nodes.get("[data-setup-login]")!.hidden.has("d-none"), true);
    assert.equal(nodes.get("[data-setup-prepare]")!.hidden.has("d-none"), true);
    assert.match(nodes.get("[data-setup-status]")!.textContent, /общий доступ закрыт/);
    assert.ok(calls.every(call => call.url.startsWith("/api/setup/")));
});

test("lost prepare response clears common password and does not repeat the mutation automatically", async () => {
    // Arrange
    fixture();
    respond = url => {
        if (url.endsWith("/prepare")) throw new TypeError("Связь потеряна");
        return { success: true, data: url.endsWith("/session") ? { authenticated: true, token: "csrf" }
            : { ...saved, attemptId: null, phase: "SetupReady" } };
    };
    initialize!(); await flush();
    nodes.get("[data-setup-prepare]")!.fields = { mode: "Bootstrap", commonLogin: "common", commonPassword: "private", botId: "123" };
    nodes.get("#setup-common-password")!.value = "private";

    // Act
    nodes.get("[data-setup-prepare]")!.submit();
    nodes.get("[data-setup-prepare]")!.submit(); await flush();

    // Assert
    assert.equal(calls.filter(call => call.url.endsWith("/prepare")).length, 1);
    assert.equal(nodes.get("#setup-common-password")!.value, "");
    assert.match(nodes.get("[data-setup-message]")!.textContent, /Связь потеряна/);
    assert.equal(nodes.get("button")!.disabled, false);
});
