export interface SearchableMultiSelectOptions {
    buttonPlaceholder?: string;
    searchPlaceholder?: string;
    allText?: string;
    clearText?: string;
    emptyText?: string;
    noOptionsText?: string;
    maxListHeight?: string;
}

export interface SearchableMultiSelectController {
    refresh(): void;
    close(): void;
}

const DEFAULT_BUTTON_PLACEHOLDER = "Не выбрано";
const DEFAULT_SEARCH_PLACEHOLDER = "Поиск";
const DEFAULT_ALL_TEXT = "Все";
const DEFAULT_CLEAR_TEXT = "Снять";
const DEFAULT_EMPTY_TEXT = "Ничего не найдено.";
const DEFAULT_MAX_LIST_HEIGHT = "14rem";

export function createSearchableMultiSelect(select: HTMLSelectElement, options: SearchableMultiSelectOptions = {}): SearchableMultiSelectController {
    if (!select.id) {
        throw new Error("Searchable multi-select requires source select id.");
    }

    if (!select.name) {
        throw new Error("Searchable multi-select requires source select name.");
    }

    if (!select.multiple) {
        throw new Error(`Searchable multi-select source must have multiple attribute: ${select.id}.`);
    }

    if (select.dataset.searchableMultiSelect === "true") {
        throw new Error(`Searchable multi-select is already initialized: ${select.id}.`);
    }

    select.dataset.searchableMultiSelect = "true";
    select.classList.add("d-none");

    const WRAPPER = document.createElement("div");
    WRAPPER.classList.add("position-relative", "w-100");

    const BUTTON = createToggleButton(select, options.buttonPlaceholder ?? DEFAULT_BUTTON_PLACEHOLDER);
    BUTTON.id = `${select.id}Button`;
    for (const LABEL of Array.from(select.labels ?? [])) {
        LABEL.htmlFor = BUTTON.id;
    }
    const PANEL = createPanel();
    const ACTIONS = createActions(
        options.allText ?? DEFAULT_ALL_TEXT,
        options.clearText ?? DEFAULT_CLEAR_TEXT,
        selectAllOptions,
        clearSelectedOptions
    );
    const SEARCH_INPUT = createSearchInput(select, options.searchPlaceholder ?? DEFAULT_SEARCH_PLACEHOLDER);
    const LIST = document.createElement("div");
    LIST.classList.add("overflow-auto");
    LIST.style.maxHeight = options.maxListHeight ?? DEFAULT_MAX_LIST_HEIGHT;

    const EMPTY = document.createElement("div");
    EMPTY.classList.add("text-muted", "small", "px-2", "py-2", "d-none");
    EMPTY.textContent = options.emptyText ?? DEFAULT_EMPTY_TEXT;

    PANEL.append(ACTIONS, SEARCH_INPUT, LIST, EMPTY);
    WRAPPER.append(BUTTON, PANEL);
    const LOADING_BUTTON = select.nextElementSibling;
    select.insertAdjacentElement("afterend", WRAPPER);
    if (LOADING_BUTTON instanceof HTMLButtonElement && LOADING_BUTTON.dataset.searchableMultiSelectLoading === select.id) {
        LOADING_BUTTON.remove();
    }

    const CONTROLLER: SearchableMultiSelectController = {
        refresh: renderOptions,
        close: closePanel
    };

    BUTTON.addEventListener("click", () => {
        if (BUTTON.disabled) {
            return;
        }

        if (PANEL.classList.contains("d-none")) {
            openPanel();
            return;
        }

        closePanel();
    });
    SEARCH_INPUT.addEventListener("input", () => filterOptions(SEARCH_INPUT, LIST, EMPTY));
    SEARCH_INPUT.addEventListener("keydown", EVENT => {
        if (EVENT.key === "Escape") {
            closePanel();
        }
    });
    select.addEventListener("change", updateSelectedState);

    renderOptions();
    updateSelectedState();

    return CONTROLLER;

    function renderOptions(): void {
        LIST.replaceChildren();

        for (const OPTION of Array.from(select.options)) {
            LIST.append(createCheckboxRow(select, OPTION));
        }

        filterOptions(SEARCH_INPUT, LIST, EMPTY);
        updateSelectedState();
    }

    function selectAllOptions(): void {
        for (const OPTION of Array.from(select.options)) {
            if (!OPTION.disabled) {
                OPTION.selected = true;
            }
        }

        select.dispatchEvent(new Event("change", { bubbles: true }));
    }

    function clearSelectedOptions(): void {
        for (const OPTION of Array.from(select.options)) {
            OPTION.selected = false;
        }

        select.dispatchEvent(new Event("change", { bubbles: true }));
    }

    function openPanel(): void {
        if (BUTTON.disabled) {
            return;
        }

        PANEL.classList.remove("d-none");
        BUTTON.setAttribute("aria-expanded", "true");
        SEARCH_INPUT.value = "";
        filterOptions(SEARCH_INPUT, LIST, EMPTY);
        window.setTimeout(() => SEARCH_INPUT.focus(), 0);
        document.addEventListener("pointerdown", handleDocumentPointerDown, true);
    }

    function closePanel(): void {
        PANEL.classList.add("d-none");
        BUTTON.setAttribute("aria-expanded", "false");
        document.removeEventListener("pointerdown", handleDocumentPointerDown, true);
    }

    function handleDocumentPointerDown(event: PointerEvent): void {
        const TARGET = event.target;
        if (!(TARGET instanceof Node)) {
            return;
        }

        if (WRAPPER.contains(TARGET)) {
            return;
        }

        closePanel();
    }

    function updateSelectedState(): void {
        const NO_OPTIONS = Array.from(select.options).every(OPTION => OPTION.disabled || OPTION.value === "");
        BUTTON.disabled = select.disabled || NO_OPTIONS;
        for (const CONTROL of PANEL.querySelectorAll<HTMLInputElement | HTMLButtonElement>("input, button")) {
            CONTROL.disabled = BUTTON.disabled;
        }
        for (const INPUT of LIST.querySelectorAll<HTMLInputElement>("input")) {
            INPUT.disabled = BUTTON.disabled || Array.from(select.options).some(OPTION => OPTION.value === INPUT.value && OPTION.disabled);
        }
        if (BUTTON.disabled) {
            closePanel();
        }

        updateButtonText(select, BUTTON, options.buttonPlaceholder ?? DEFAULT_BUTTON_PLACEHOLDER, options.allText ?? DEFAULT_ALL_TEXT);
        if (NO_OPTIONS) {
            const TEXT = BUTTON.querySelector<HTMLElement>("[data-searchable-multi-select-button-text]");
            if (!TEXT) throw new Error("Текст поля выбора не найден.");
            TEXT.textContent = options.noOptionsText ?? "Нет объектов.";
        }

        for (const CHECKBOX of LIST.querySelectorAll<HTMLInputElement>("input[type='checkbox']")) {
            const OPTION = findOption(select, CHECKBOX.value);
            CHECKBOX.checked = OPTION.selected;
        }
    }
}

function createToggleButton(select: HTMLSelectElement, placeholder: string): HTMLButtonElement {
    const BUTTON = document.createElement("button");
    BUTTON.type = "button";
    BUTTON.classList.add("btn", "btn-outline-secondary", "dropdown-toggle", "w-100", "d-flex", "align-items-center", "justify-content-between", "text-start");
    BUTTON.setAttribute("aria-haspopup", "listbox");
    BUTTON.setAttribute("aria-expanded", "false");
    BUTTON.setAttribute("data-searchable-multi-select-button", select.id);

    const TEXT = document.createElement("span");
    TEXT.dataset.searchableMultiSelectButtonText = "";
    TEXT.textContent = placeholder;

    BUTTON.append(TEXT);
    return BUTTON;
}

function createPanel(): HTMLElement {
    const PANEL = document.createElement("div");
    PANEL.classList.add("position-absolute", "start-0", "end-0", "mt-1", "border", "rounded-2", "shadow", "bg-body", "p-2", "d-none");
    PANEL.style.zIndex = "1050";
    return PANEL;
}

function createActions(allText: string, clearText: string, onSelectAll: () => void, onClear: () => void): HTMLElement {
    const ACTIONS = document.createElement("div");
    ACTIONS.classList.add("d-flex", "gap-2", "mb-2");

    const ALL_BUTTON = document.createElement("button");
    ALL_BUTTON.type = "button";
    ALL_BUTTON.classList.add("btn", "btn-sm", "btn-outline-primary", "flex-fill");
    ALL_BUTTON.textContent = allText;
    ALL_BUTTON.addEventListener("click", onSelectAll);

    const CLEAR_BUTTON = document.createElement("button");
    CLEAR_BUTTON.type = "button";
    CLEAR_BUTTON.classList.add("btn", "btn-sm", "btn-outline-secondary", "flex-fill");
    CLEAR_BUTTON.textContent = clearText;
    CLEAR_BUTTON.addEventListener("click", onClear);

    ACTIONS.append(ALL_BUTTON, CLEAR_BUTTON);
    return ACTIONS;
}

function createSearchInput(select: HTMLSelectElement, placeholder: string): HTMLInputElement {
    const INPUT = document.createElement("input");
    INPUT.type = "search";
    INPUT.id = `${select.id}SearchableMultiSearch`;
    INPUT.name = `${select.id}SearchableMultiSearch`;
    INPUT.classList.add("form-control", "form-control-sm", "mb-2");
    INPUT.placeholder = placeholder;
    INPUT.autocomplete = "off";
    return INPUT;
}

function createCheckboxRow(select: HTMLSelectElement, option: HTMLOptionElement): HTMLElement {
    const ROW = document.createElement("div");
    ROW.classList.add("form-check", "d-flex", "align-items-start", "gap-2", "px-2", "py-1", "m-0");
    ROW.toggleAttribute("hidden", option.hidden === true);

    const CHECKBOX = document.createElement("input");
    CHECKBOX.type = "checkbox";
    CHECKBOX.id = `${select.id}SearchableMulti_${sanitizeIdPart(option.value)}`;
    CHECKBOX.name = `${select.id}SearchableMultiValue`;
    CHECKBOX.classList.add("form-check-input", "flex-shrink-0", "mt-1", "ms-0");
    CHECKBOX.value = option.value;
    CHECKBOX.checked = option.selected;
    CHECKBOX.disabled = option.disabled;

    const LABEL = document.createElement("label");
    LABEL.classList.add("form-check-label", "flex-grow-1", "text-break");
    LABEL.htmlFor = CHECKBOX.id;
    LABEL.textContent = option.textContent ?? option.value;

    CHECKBOX.addEventListener("change", () => {
        option.selected = CHECKBOX.checked;
        select.dispatchEvent(new Event("change", { bubbles: true }));
    });

    ROW.append(CHECKBOX, LABEL);
    return ROW;
}

function filterOptions(input: HTMLInputElement, list: HTMLElement, empty: HTMLElement): void {
    const QUERY = input.value.trim().toLocaleLowerCase("ru-RU");
    let visibleCount = 0;

    for (const ROW of Array.from(list.children)) {
        if (!(ROW instanceof HTMLElement)) {
            continue;
        }

        if (ROW.hidden) {
            continue;
        }

        const LABEL = ROW.querySelector("label");
        const TEXT = LABEL?.textContent?.toLocaleLowerCase("ru-RU") ?? "";
        const IS_VISIBLE = QUERY.length === 0 || TEXT.includes(QUERY);
        ROW.classList.toggle("d-none", !IS_VISIBLE);

        if (IS_VISIBLE) {
            visibleCount++;
        }
    }

    empty.classList.toggle("d-none", visibleCount > 0);
}

function updateButtonText(select: HTMLSelectElement, button: HTMLButtonElement, placeholder: string, allText: string): void {
    const TEXT = button.querySelector<HTMLElement>("[data-searchable-multi-select-button-text]");
    if (TEXT === null) {
        throw new Error("Searchable multi-select button text element was not found.");
    }

    const SELECTABLE_OPTIONS = Array.from(select.options).filter(OPTION => !OPTION.disabled);
    const SELECTED_OPTIONS = SELECTABLE_OPTIONS.filter(OPTION => OPTION.selected);
    if (SELECTED_OPTIONS.length === 0) {
        TEXT.textContent = placeholder;
        return;
    }

    if (SELECTED_OPTIONS.length === SELECTABLE_OPTIONS.length) {
        TEXT.textContent = allText;
        return;
    }

    if (SELECTED_OPTIONS.length === 1) {
        TEXT.textContent = SELECTED_OPTIONS.map(OPTION => OPTION.textContent ?? OPTION.value).join(", ");
        return;
    }

    TEXT.textContent = `Выбрано: ${SELECTED_OPTIONS.length}`;
}

function findOption(select: HTMLSelectElement, value: string): HTMLOptionElement {
    const OPTION = Array.from(select.options).find(ITEM => ITEM.value === value);
    if (OPTION === undefined) {
        throw new Error(`Searchable multi-select option was not found: ${value}.`);
    }

    return OPTION;
}

function sanitizeIdPart(value: string): string {
    const SANITIZED = value.replace(/[^a-zA-Z0-9_-]/g, "_");
    return SANITIZED.length > 0
        ? SANITIZED
        : "empty";
}
