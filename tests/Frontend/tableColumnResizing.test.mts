import assert from "node:assert/strict";
import { test } from "node:test";
import { initTableColumnResizing } from "../../UptimeHarbor.Web/wwwroot/ts/shared/tableColumnResizing.ts";

class FakeElement extends EventTarget {
    dataset: Record<string, string> = {};
    style: Record<string, string> = {};
    children: FakeElement[] = [];
    textContent = "Column";
    classes = new Set<string>();
    classList = {
        add: (...names: string[]) => names.forEach(name => this.classes.add(name)),
        remove: (...names: string[]) => names.forEach(name => this.classes.delete(name)),
        contains: (name: string) => this.classes.has(name),
        toggle: (name: string, force: boolean) => force ? this.classes.add(name) : this.classes.delete(name)
    };
    table!: FakeTable;
    append(child: FakeElement) { this.children.push(child); }
    querySelector() { return this.children[0] ?? null; }
    closest() { return this.table; }
    setPointerCapture() {}
    hasPointerCapture() { return false; }
    matches() { return false; }
    getBoundingClientRect() {
        const extra = this.table.style.minWidth === "100%" ? 100 : 0;
        return { width: Number.parseFloat(this.style.width) + extra };
    }
}

class FakeTable {
    dataset = { tableKey: "servers" };
    style = { minWidth: "100%", width: "401px" };
    headers = [new FakeElement(), new FakeElement()];
    tHead = { querySelectorAll: () => this.headers };
    constructor() {
        this.headers.forEach((header, index) => {
            header.dataset.columnKey = String(index);
            header.style.width = "200px";
            header.table = this;
        });
    }
    querySelectorAll() { return this.headers; }
    getClientRects() { return [{}]; }
    getBoundingClientRect() {
        return { width: this.style.minWidth === "100%" ? 601 : Number.parseFloat(this.style.width) };
    }
}

class FakeObserver {
    observe() {}
    disconnect() {}
}

function setup() {
    const storage = new Map<string, string>();
    Object.assign(globalThis, {
        document: { createElement: () => new FakeElement(), documentElement: new FakeElement(), activeElement: null },
        localStorage: {
            getItem: (key: string) => storage.get(key) ?? null,
            setItem: (key: string, value: string) => storage.set(key, value)
        },
        MutationObserver: FakeObserver,
        ResizeObserver: FakeObserver
    });
    const table = new FakeTable();
    const controller = initTableColumnResizing(table as unknown as HTMLTableElement, { pageKey: "objects" });
    return { table, handle: table.headers[0].children[0], storage, controller };
}

function pointer(handle: FakeElement, type: string, clientX: number, pointerId = 1) {
    const event = new Event(type, { cancelable: true });
    Object.assign(event, { clientX, pointerId, button: 0 });
    handle.dispatchEvent(event);
}

function widths(table: FakeTable) {
    return table.headers.map(header => header.getBoundingClientRect().width);
}

test("drag starts at rendered geometry and changes only the selected column by the pointer delta", () => {
    const { table, handle, storage, controller } = setup();
    assert.deepEqual(widths(table), [300, 300]);

    pointer(handle, "pointerdown", 300);
    assert.deepEqual(widths(table), [300, 300]);
    assert.equal(table.style.width, "601px");

    pointer(handle, "pointermove", 340);
    assert.deepEqual(widths(table), [340, 300]);
    assert.equal(table.style.width, "641px");
    pointer(handle, "pointerup", 340);
    assert.deepEqual([...storage.values()], ["340", "300"]);

    pointer(handle, "pointerdown", 340);
    assert.deepEqual(widths(table), [340, 300]);
    pointer(handle, "pointermove", 355.5);
    assert.deepEqual(widths(table), [355.5, 300]);
    pointer(handle, "pointerup", 355.5);
    controller.destroy();
});

test("cancellation restores the rendered start width and ignores other pointers", () => {
    const { table, handle, storage, controller } = setup();
    pointer(handle, "pointerdown", 300);
    pointer(handle, "pointermove", 400, 2);
    assert.deepEqual(widths(table), [300, 300]);

    pointer(handle, "pointermove", 380);
    pointer(handle, "pointercancel", 380);
    assert.deepEqual(widths(table), [300, 300]);
    assert.equal(table.style.width, "601px");
    assert.equal(storage.size, 0);
    pointer(handle, "pointermove", 400);
    assert.deepEqual(widths(table), [300, 300]);
    controller.destroy();
});

test("keyboard resizing preserves neighbouring columns and respects the minimum width", () => {
    const { table, handle, controller } = setup();
    const right = new Event("keydown", { cancelable: true });
    Object.assign(right, { key: "ArrowRight" });
    handle.dispatchEvent(right);
    assert.deepEqual(widths(table), [316, 300]);

    pointer(handle, "pointerdown", 316);
    pointer(handle, "pointermove", -1000);
    assert.deepEqual(widths(table), [96, 300]);
    pointer(handle, "pointerup", -1000);
    controller.destroy();
});
