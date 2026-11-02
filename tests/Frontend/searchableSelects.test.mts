import assert from "node:assert/strict";
import { beforeEach, test } from "node:test";
import { createSearchableMultiSelect } from "../../UptimeHarbor.Web/wwwroot/ts/shared/searchableMultiSelect.ts";
import { createSearchableRadioSelect } from "../../UptimeHarbor.Web/wwwroot/ts/shared/searchableRadioSelect.ts";

// Заглушка проверяет состояние, события и заданную геометрию; Bootstrap проверяют в браузере.
class ElementStub extends EventTarget {
    children: ElementStub[] = [];
    parent: ElementStub | null = null;
    dataset: Record<string, string> = {};
    style: Record<string, string> = {};
    attributes = new Map<string, string>();
    classes = new Set<string>();
    classList = {
        add: (...values: string[]) => values.forEach(value => this.classes.add(value)),
        remove: (...values: string[]) => values.forEach(value => this.classes.delete(value)),
        contains: (value: string) => this.classes.has(value),
        toggle: (value: string, force: boolean) => force ? this.classes.add(value) : this.classes.delete(value)
    };
    id = "";
    name = "";
    type = "";
    value = "";
    textContent = "";
    multiple = false;
    hidden = false;
    disabled = false;
    selected = false;
    checked = false;
    options: ElementStub[] = [];
    labels: ElementStub[] = [];
    htmlFor = "";
    tag: string;
    rect = { left: 100, top: 400, bottom: 440, width: 300 };
    offsetHeight = 240;

    constructor(tag: string) { super(); this.tag = tag; }
    get selectedOptions() {
        const selected = this.options.filter(option => this.multiple ? option.selected : option.value === this.value);
        return { item: (index: number) => selected[index] ?? null };
    }
    get nextElementSibling() {
        return this.parent?.children[this.parent.children.indexOf(this) + 1] ?? null;
    }
    append(...children: ElementStub[]) {
        for (const child of children) { child.parent = this; this.children.push(child); }
    }
    replaceChildren() { this.children = []; }
    insertAdjacentElement(position: string, element: ElementStub) {
        assert.equal(position, "afterend");
        element.parent = this.parent;
        this.parent!.children.splice(this.parent!.children.indexOf(this) + 1, 0, element);
    }
    setAttribute(name: string, value: string) { this.attributes.set(name, value); }
    toggleAttribute(name: string, value: boolean) { if (name === "hidden") this.hidden = value; }
    focus() {}
    closest(selector: string): ElementStub | null {
        assert.equal(selector, ".modal");
        if (this.classes.has("modal")) return this;
        return this.parent?.closest(selector) ?? null;
    }
    getBoundingClientRect() { return this.rect; }
    contains(target: ElementStub): boolean { return this === target || this.descendants().includes(target); }
    descendants(): ElementStub[] { return this.children.flatMap(child => [child, ...child.descendants()]); }
    querySelector(selector: string) { return this.querySelectorAll(selector)[0] ?? null; }
    querySelectorAll(selector: string): ElementStub[] {
        const descendants = this.descendants();
        if (selector === "input, button") return descendants.filter(child => child.tag === "input" || child.tag === "button");
        if (selector === "input") return descendants.filter(child => child.tag === "input");
        if (selector === "label") return descendants.filter(child => child.tag === "label");
        if (selector === "input[type='checkbox']") return descendants.filter(child => child.type === "checkbox");
        if (selector === "input[type='radio']") return descendants.filter(child => child.type === "radio");
        if (selector === "[data-searchable-multi-select-button-text]") return descendants.filter(child => "searchableMultiSelectButtonText" in child.dataset);
        if (selector === "[data-searchable-radio-select-button-text]") return descendants.filter(child => "searchableRadioSelectButtonText" in child.dataset);
        throw new Error(`Неожиданный селектор: ${selector}`);
    }
}

beforeEach(() => {
    Object.assign(globalThis, {
        Node: ElementStub,
        HTMLElement: ElementStub,
        HTMLButtonElement: ElementStub,
        document: Object.assign(new EventTarget(), { createElement: (tag: string) => new ElementStub(tag) }),
        window: Object.assign(new EventTarget(), {
            setTimeout: (action: () => void) => action(), innerWidth: 1024, innerHeight: 768
        })
    });
});

function source(multiple = false) {
    const select = new ElementStub("select");
    select.id = "objects";
    select.name = "objectIds";
    select.multiple = multiple;
    const label = new ElementStub("label");
    label.htmlFor = select.id;
    select.labels.push(label);
    const host = new ElementStub("div");
    host.append(select);
    return select;
}

function option(value: string, text: string, selected = false) {
    const result = new ElementStub("option");
    Object.assign(result, { value, textContent: text, selected });
    return result;
}

function controls(select: ElementStub) {
    const wrapper = select.nextElementSibling!;
    return { wrapper, button: wrapper.children[0], panel: wrapper.children[1] };
}

test("radio: modal panel escapes the scrollable body and stays selectable above the footer", () => {
    // Arrange
    const select = source();
    select.options = [option("server", "Сервер")];
    const modal = new ElementStub("div");
    modal.classList.add("modal");
    modal.append(select.parent!);
    const controller = createSearchableRadioSelect(select as unknown as HTMLSelectElement);
    const button = select.nextElementSibling!.children[0];

    // Act
    button.dispatchEvent(new Event("click"));

    // Assert
    const panel = modal.children.find(child => child !== select.parent)!;
    assert.ok(panel, "The panel must be outside the clipped modal body.");
    assert.equal(panel.classList.contains("position-fixed"), true);
    assert.equal(panel.classList.contains("d-none"), false);
    assert.equal(panel.style.left, "100px");
    assert.equal(panel.style.width, "300px");
    assert.equal(panel.style.top, "444px");

    // Act
    const radio = panel.querySelectorAll("input[type='radio']")[0];
    const pointerDown = new Event("pointerdown");
    Object.defineProperty(pointerDown, "target", { value: radio });
    document.dispatchEvent(pointerDown);

    // Assert
    assert.equal(panel.classList.contains("d-none"), false);

    // Act
    radio.checked = true;
    radio.dispatchEvent(new Event("change"));

    // Assert
    assert.equal(select.value, "server");
    assert.equal(panel.classList.contains("d-none"), true);

    // Act
    button.rect = { left: 100, top: 700, bottom: 740, width: 300 };
    button.dispatchEvent(new Event("click"));

    // Assert
    assert.equal(panel.style.top, "456px");

    // Act
    window.dispatchEvent(new Event("resize"));

    // Assert
    assert.equal(panel.classList.contains("d-none"), true);

    // Act
    button.dispatchEvent(new Event("click"));
    document.dispatchEvent(new Event("scroll"));

    // Assert
    assert.equal(panel.classList.contains("d-none"), true);

    // Act
    button.dispatchEvent(new Event("click"));
    modal.dispatchEvent(new Event("hide.bs.modal"));

    // Assert
    assert.equal(panel.classList.contains("d-none"), true);
    controller.close();
});

test("radio: filter panel can be wider without widening the toggle", () => {
    // Arrange
    const select = source();
    select.options = [option("", "Все состояния"), option("enabled", "Включены")];

    // Act
    createSearchableRadioSelect(select as unknown as HTMLSelectElement, { panelMinWidth: "14rem" });
    const { button, panel } = controls(select);
    button.dispatchEvent(new Event("click"));

    // Assert
    assert.equal(panel.style.minWidth, "14rem");
    assert.equal(button.style.minWidth, undefined);
    assert.equal(panel.classList.contains("d-none"), false);

    // Act
    const pointerDown = new Event("pointerdown");
    Object.defineProperty(pointerDown, "target", { value: new ElementStub("div") });
    document.dispatchEvent(pointerDown);

    // Assert
    assert.equal(panel.classList.contains("d-none"), true);
});

test("radio: empty choices disable the field and show Нет объектов; refresh enables it", () => {
    const select = source();
    const controller = createSearchableRadioSelect(select as unknown as HTMLSelectElement);
    const { wrapper, button, panel } = controls(select);
    assert.equal(button.disabled, true);
    assert.equal(wrapper.querySelector("[data-searchable-radio-select-button-text]")!.textContent, "Нет объектов.");
    button.dispatchEvent(new Event("click"));
    assert.equal(panel.classList.contains("d-none"), true);
    assert.equal(select.labels[0].htmlFor, button.id);

    select.options = [option("first", "Первый")];
    select.value = "first";
    controller.refresh();
    assert.equal(button.disabled, false);
    assert.equal(wrapper.querySelector("[data-searchable-radio-select-button-text]")!.textContent, "Первый");
});

test("radio: selecting Без сервера clears the link and dispatches change", () => {
    const select = source();
    select.options = [option("", "Без сервера"), option("server", "Сервер")];
    select.value = "server";
    const controller = createSearchableRadioSelect(select as unknown as HTMLSelectElement);
    let changes = 0;
    select.addEventListener("change", () => changes++);
    const { wrapper } = controls(select);
    const none = wrapper.querySelectorAll("input[type='radio']").find(input => input.value === "")!;
    none.checked = true;
    none.dispatchEvent(new Event("change"));
    assert.equal(select.value, "");
    assert.equal(changes, 1);
    assert.equal(wrapper.querySelector("[data-searchable-radio-select-button-text]")!.textContent, "Без сервера");
    controller.refresh();
    assert.equal(wrapper.querySelectorAll("input[type='radio']")[0].checked, true);
});

test("radio: saving and an empty result keep every generated control disabled", () => {
    const select = source();
    select.options = [option("server", "Сервер")];
    select.value = "server";
    const controller = createSearchableRadioSelect(select as unknown as HTMLSelectElement);
    select.disabled = true;
    controller.refresh();
    const { wrapper, button } = controls(select);
    assert.equal(button.disabled, true);
    assert.ok(wrapper.querySelectorAll("input").every(input => input.disabled));
    select.disabled = false;
    select.options = [];
    controller.refresh();
    assert.equal(button.disabled, true);
});

test("multi: empty choices disable the button; checkboxes add and remove a link", () => {
    const select = source(true);
    const controller = createSearchableMultiSelect(select as unknown as HTMLSelectElement);
    const { wrapper, button } = controls(select);
    assert.equal(button.disabled, true);
    assert.equal(wrapper.querySelector("[data-searchable-multi-select-button-text]")!.textContent, "Нет объектов.");
    select.options = [option("one", "Первый"), option("two", "Второй")];
    controller.refresh();
    assert.equal(button.disabled, false);
    const checkbox = wrapper.querySelectorAll("input[type='checkbox']")[0];
    checkbox.checked = true;
    checkbox.dispatchEvent(new Event("change"));
    assert.equal(select.options[0].selected, true);
    checkbox.checked = false;
    checkbox.dispatchEvent(new Event("change"));
    assert.equal(select.options[0].selected, false);
});

test("multi: refresh preserves disabled options and saving prevents opening", () => {
    const select = source(true);
    const disabled = option("busy", "Занят");
    disabled.disabled = true;
    select.options = [option("free", "Свободен", true), disabled];
    const controller = createSearchableMultiSelect(select as unknown as HTMLSelectElement);
    const { wrapper, button, panel } = controls(select);
    assert.equal(wrapper.querySelectorAll("input[type='checkbox']")[1].disabled, true);
    select.disabled = true;
    controller.refresh();
    button.dispatchEvent(new Event("click"));
    assert.equal(panel.classList.contains("d-none"), true);
    assert.equal(select.options[0].selected, true);
    select.disabled = false;
    controller.refresh();
    assert.equal(button.disabled, false);
    assert.equal(wrapper.querySelectorAll("input[type='checkbox']")[1].disabled, true);
});
