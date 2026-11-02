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
        const value = Number.parseFloat(this.style.width);
        return { width: this.style.width.endsWith("%") ? value * this.table.contentWidth / 100 : value };
    }
}

class FakeTable {
    dataset = { tableKey: "servers" };
    style = { minWidth: "100%", width: "100%" };
    contentWidth = 600;
    headers: FakeElement[];
    cells: FakeElement[] = [];
    tHead = { querySelectorAll: () => this.headers };
    constructor(columnCount = 2) {
        this.headers = Array.from({ length: columnCount }, () => new FakeElement());
        this.headers.forEach((header, index) => {
            header.dataset.columnKey = String(index);
            header.style.width = `${100 / columnCount}%`;
            header.dataset.columnDefaultWidth = "200";
            header.table = this;
        });
    }
    querySelectorAll() { return [...this.headers, ...this.cells]; }
    getClientRects() { return [{}]; }
    getBoundingClientRect() {
        return { width: this.style.width === "100%" ? this.contentWidth + 1 : Number.parseFloat(this.style.width) };
    }
}

class FakeObserver {
    observe() {}
    disconnect() {}
}

function setup(savedWidths: number[] = [], columnCount = 2) {
    const storage = new Map<string, string>();
    savedWidths.forEach((width, index) => storage.set(`uptimeHarborTableColumnWidth:objects:servers:${index}`, String(width)));
    Object.assign(globalThis, {
        document: { createElement: () => new FakeElement(), documentElement: new FakeElement(), activeElement: null },
        localStorage: {
            getItem: (key: string) => storage.get(key) ?? null,
            setItem: (key: string, value: string) => storage.set(key, value)
        },
        MutationObserver: FakeObserver,
        ResizeObserver: FakeObserver
    });
    const table = new FakeTable(columnCount);
    const controller = initTableColumnResizing(table as unknown as HTMLTableElement, { pageKey: "objects" });
    return { table, handle: table.headers[0].children[0], storage, controller };
}

function pointer(handle: FakeElement, type: string, clientX: number, pointerId = 1) {
    const event = new Event(type, { cancelable: true });
    Object.assign(event, { clientX, pointerId, button: 0 });
    handle.dispatchEvent(event);
}

function widths(table: FakeTable) {
    // Проценты дают погрешность Number; сохраняем точность существенно меньше пикселя.
    return table.headers.map(header => Math.round(header.getBoundingClientRect().width * 1e9) / 1e9);
}

test("drag redistributes neighbouring widths without moving the table borders", () => {
    // Arrange
    const { table, handle, storage, controller } = setup();
    assert.deepEqual(widths(table), [300, 300]);

    // Act
    pointer(handle, "pointerdown", 300);

    // Assert
    assert.deepEqual(widths(table), [300, 300]);
    assert.equal(table.getBoundingClientRect().width, 601);

    // Act
    pointer(handle, "pointermove", 340);
    pointer(handle, "pointerup", 340);

    // Assert
    assert.deepEqual(widths(table), [340, 260]);
    assert.equal(table.getBoundingClientRect().width, 601);
    assert.deepEqual([...storage.values()], ["340", "260"]);

    // Act
    pointer(handle, "pointerdown", 340);
    pointer(handle, "pointermove", 355.5);
    pointer(handle, "pointerup", 355.5);

    // Assert
    assert.deepEqual(widths(table), [355.5, 244.5]);
    assert.equal(table.getBoundingClientRect().width, 601);
    controller.destroy();
});

test("cancellation restores the rendered start width and ignores other pointers", () => {
    // Arrange
    const { table, handle, storage, controller } = setup();

    // Act
    pointer(handle, "pointerdown", 300);
    pointer(handle, "pointermove", 400, 2);

    // Assert
    assert.deepEqual(widths(table), [300, 300]);

    // Act
    pointer(handle, "pointermove", 380);
    pointer(handle, "pointercancel", 380);

    // Assert
    assert.deepEqual(widths(table), [300, 300]);
    assert.equal(table.getBoundingClientRect().width, 601);
    assert.equal(storage.size, 0);

    // Act
    pointer(handle, "pointermove", 400);

    // Assert
    assert.deepEqual(widths(table), [300, 300]);
    controller.destroy();
});

test("keyboard and pointer resizing respect both neighbours' minimum widths", () => {
    // Arrange
    const { table, handle, controller } = setup();
    const right = new Event("keydown", { cancelable: true });
    Object.assign(right, { key: "ArrowRight" });

    // Act
    handle.dispatchEvent(right);

    // Assert
    assert.deepEqual(widths(table), [316, 284]);

    // Act
    pointer(handle, "pointerdown", 316);
    pointer(handle, "pointermove", -1000);
    pointer(handle, "pointerup", -1000);

    // Assert
    assert.deepEqual(widths(table), [96, 504]);

    // Act
    pointer(handle, "pointerdown", 96);
    pointer(handle, "pointermove", 2000);
    pointer(handle, "pointerup", 2000);

    // Assert
    assert.deepEqual(widths(table), [504, 96]);
    assert.equal(table.getBoundingClientRect().width, 601);
    controller.destroy();
});

test("saved pixel widths restore proportions and follow the available page width", () => {
    // Arrange
    const { table, controller } = setup([900, 300]);

    // Assert
    assert.deepEqual(widths(table), [450, 150]);
    assert.equal(table.getBoundingClientRect().width, 601);
    assert.equal(table.headers[1].children.length, 0, "The outer table border must not be draggable.");

    // Act
    table.contentWidth = 300;
    controller.refresh();

    // Assert
    assert.deepEqual(widths(table), [225, 75]);
    assert.equal(table.getBoundingClientRect().width, 301);
    controller.destroy();
});

test("resizing a boundary leaves other columns unchanged and refresh sizes new rows", () => {
    // Arrange
    const { table, handle, controller } = setup([], 3);

    // Act
    pointer(handle, "pointerdown", 200);
    pointer(handle, "pointermove", 240);
    pointer(handle, "pointerup", 240);
    const cell = new FakeElement();
    cell.table = table;
    cell.dataset.columnKey = "1";
    table.cells.push(cell);
    controller.refresh();

    // Assert
    assert.deepEqual(widths(table), [240, 160, 200]);
    assert.equal(cell.getBoundingClientRect().width, 160);
    assert.equal(table.getBoundingClientRect().width, 601);
    controller.destroy();
});

test("narrow layouts keep both neighbours visible when their starting widths are below the default minimum", () => {
    // Arrange
    const { table, handle, controller } = setup();
    table.contentWidth = 150;
    controller.refresh();

    // Act
    pointer(handle, "pointerdown", 75);
    pointer(handle, "pointermove", 1000);
    pointer(handle, "pointerup", 1000);

    // Assert
    assert.deepEqual(widths(table), [75, 75]);
    assert.equal(table.getBoundingClientRect().width, 151);
    controller.destroy();
});
