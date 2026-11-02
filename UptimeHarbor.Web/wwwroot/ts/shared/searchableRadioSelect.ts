export interface SearchableRadioSelectOptions {
    buttonPlaceholder?: string;
    searchPlaceholder?: string;
    emptyText?: string;
    noOptionsText?: string;
    maxListHeight?: string;
    panelMinWidth?: string;
}

export interface SearchableRadioSelectController {
    close(): void;
    refresh(): void;
}

const DEFAULT_BUTTON_PLACEHOLDER = "Выберите значение";
const DEFAULT_SEARCH_PLACEHOLDER = "Поиск";
const DEFAULT_EMPTY_TEXT = "Ничего не найдено.";
const DEFAULT_MAX_LIST_HEIGHT = "14rem";

export function createSearchableRadioSelect(select: HTMLSelectElement, options: SearchableRadioSelectOptions = {}): SearchableRadioSelectController {
    if (!select.id) {
        throw new Error("Searchable radio select requires source select id.");
    }

    if (!select.name) {
        throw new Error("Searchable radio select requires source select name.");
    }

    if (select.dataset.searchableRadioSelect === "true") {
        throw new Error(`Searchable radio select is already initialized: ${select.id}.`);
    }

    select.dataset.searchableRadioSelect = "true";
    select.classList.add("d-none");

    const WRAPPER = document.createElement("div");
    WRAPPER.classList.add("position-relative");

    const BUTTON = createToggleButton(select, options.buttonPlaceholder ?? DEFAULT_BUTTON_PLACEHOLDER);
    BUTTON.id = `${select.id}Button`;
    for (const LABEL of Array.from(select.labels ?? [])) {
        LABEL.htmlFor = BUTTON.id;
    }
    const MODAL = select.closest<HTMLElement>(".modal");
    const PANEL = createPanel(MODAL !== null);
    if (options.panelMinWidth) PANEL.style.minWidth = options.panelMinWidth;
    const SEARCH_INPUT = createSearchInput(select, options.searchPlaceholder ?? DEFAULT_SEARCH_PLACEHOLDER);
    const LIST = document.createElement("div");
    LIST.classList.add("overflow-auto");
    LIST.style.maxHeight = options.maxListHeight ?? DEFAULT_MAX_LIST_HEIGHT;

    const EMPTY = document.createElement("div");
    EMPTY.classList.add("text-muted", "small", "px-2", "py-2", "d-none");
    EMPTY.textContent = options.emptyText ?? DEFAULT_EMPTY_TEXT;

    PANEL.append(SEARCH_INPUT, LIST, EMPTY);
    WRAPPER.append(BUTTON);
    // Остаёмся внутри focus trap modal, но вне обрезающей scroll-области.
    (MODAL ?? WRAPPER).append(PANEL);
    select.insertAdjacentElement("afterend", WRAPPER);
    MODAL?.addEventListener("hide.bs.modal", closePanel);

    const CONTROLLER: SearchableRadioSelectController = {
        close: closePanel,
        refresh: renderOptions
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
            LIST.append(createRadioRow(select, OPTION, closePanel));
        }

        filterOptions(SEARCH_INPUT, LIST, EMPTY);
        updateSelectedState();
    }

    function openPanel(): void {
        if (BUTTON.disabled) {
            return;
        }

        PANEL.classList.remove("d-none");
        BUTTON.setAttribute("aria-expanded", "true");
        SEARCH_INPUT.value = "";
        filterOptions(SEARCH_INPUT, LIST, EMPTY);
        if (MODAL) positionModalPanel();
        window.setTimeout(() => SEARCH_INPUT.focus(), 0);
        document.addEventListener("pointerdown", handleDocumentPointerDown, true);
        if (MODAL) {
            window.addEventListener("resize", closePanel);
            document.addEventListener("scroll", handleDocumentScroll, true);
        }
    }

    function closePanel(): void {
        PANEL.classList.add("d-none");
        BUTTON.setAttribute("aria-expanded", "false");
        document.removeEventListener("pointerdown", handleDocumentPointerDown, true);
        window.removeEventListener("resize", closePanel);
        document.removeEventListener("scroll", handleDocumentScroll, true);
    }

    function positionModalPanel(): void {
        const RECT = BUTTON.getBoundingClientRect();
        const MARGIN = 8;
        const GAP = 4;
        const WIDTH = Math.min(RECT.width, window.innerWidth - MARGIN * 2);
        PANEL.style.width = `${WIDTH}px`;
        PANEL.style.left = `${Math.max(MARGIN, Math.min(RECT.left, window.innerWidth - WIDTH - MARGIN))}px`;
        LIST.style.maxHeight = options.maxListHeight ?? DEFAULT_MAX_LIST_HEIGHT;
        const BELOW = window.innerHeight - RECT.bottom - GAP - MARGIN;
        const ABOVE = RECT.top - GAP - MARGIN;
        const OPEN_ABOVE = PANEL.offsetHeight > BELOW && ABOVE > BELOW;
        const CHROME_HEIGHT = PANEL.offsetHeight - LIST.offsetHeight;
        const AVAILABLE = Math.max(0, (OPEN_ABOVE ? ABOVE : BELOW) - CHROME_HEIGHT);
        LIST.style.maxHeight = `min(${options.maxListHeight ?? DEFAULT_MAX_LIST_HEIGHT}, ${AVAILABLE}px)`;
        PANEL.style.top = `${OPEN_ABOVE ? Math.max(MARGIN, RECT.top - PANEL.offsetHeight - GAP) : RECT.bottom + GAP}px`;
    }

    function handleDocumentScroll(event: Event): void {
        if (event.target instanceof Node && PANEL.contains(event.target)) return;
        closePanel();
    }

    function handleDocumentPointerDown(event: PointerEvent): void {
        const TARGET = event.target;
        if (!(TARGET instanceof Node)) {
            return;
        }

        if (WRAPPER.contains(TARGET) || PANEL.contains(TARGET)) {
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

        updateButtonText(select, BUTTON, options.buttonPlaceholder ?? DEFAULT_BUTTON_PLACEHOLDER);
        if (NO_OPTIONS) {
            const TEXT = BUTTON.querySelector<HTMLElement>("[data-searchable-radio-select-button-text]");
            if (!TEXT) throw new Error("Текст поля выбора не найден.");
            TEXT.textContent = options.noOptionsText ?? "Нет объектов.";
        }

        for (const RADIO of LIST.querySelectorAll<HTMLInputElement>("input[type='radio']")) {
            RADIO.checked = RADIO.value === select.value;
        }
    }
}

function createToggleButton(select: HTMLSelectElement, placeholder: string): HTMLButtonElement {
    const BUTTON = document.createElement("button");
    BUTTON.type = "button";
    BUTTON.classList.add("btn", "btn-outline-secondary", "dropdown-toggle", "w-100", "d-flex", "align-items-start", "justify-content-between", "text-start", "text-wrap");
    BUTTON.setAttribute("aria-haspopup", "listbox");
    BUTTON.setAttribute("aria-expanded", "false");
    BUTTON.setAttribute("data-searchable-radio-select-button", select.id);

    const TEXT = document.createElement("span");
    TEXT.classList.add("flex-grow-1");
    TEXT.dataset.searchableRadioSelectButtonText = "";
    TEXT.style.whiteSpace = "normal";
    TEXT.style.overflowWrap = "anywhere";
    TEXT.textContent = placeholder;

    BUTTON.append(TEXT);
    return BUTTON;
}

function createPanel(inModal: boolean): HTMLElement {
    const PANEL = document.createElement("div");
    PANEL.classList.add("border", "rounded-2", "shadow", "bg-body", "p-2", "d-none");
    if (inModal) PANEL.classList.add("position-fixed");
    else PANEL.classList.add("position-absolute", "start-0", "end-0", "mt-1");
    PANEL.style.zIndex = "1050";
    return PANEL;
}

function createSearchInput(select: HTMLSelectElement, placeholder: string): HTMLInputElement {
    const INPUT = document.createElement("input");
    INPUT.type = "search";
    INPUT.id = `${select.id}SearchableRadioSearch`;
    INPUT.name = `${select.id}SearchableRadioSearch`;
    INPUT.classList.add("form-control", "form-control-sm", "mb-2");
    INPUT.placeholder = placeholder;
    INPUT.autocomplete = "off";
    return INPUT;
}

function createRadioRow(select: HTMLSelectElement, option: HTMLOptionElement, onSelect: () => void): HTMLElement {
    const ROW = document.createElement("div");
    ROW.classList.add("d-flex", "align-items-start", "gap-2", "px-2", "py-1");
    ROW.toggleAttribute("hidden", option.hidden === true);

    const RADIO = document.createElement("input");
    RADIO.type = "radio";
    RADIO.id = `${select.id}SearchableRadio_${sanitizeIdPart(option.value)}`;
    RADIO.name = `${select.id}SearchableRadioValue`;
    RADIO.classList.add("form-check-input", "flex-shrink-0", "m-0", "mt-1");
    RADIO.value = option.value;
    RADIO.checked = option.selected;
    RADIO.disabled = option.disabled;

    const LABEL = document.createElement("label");
    LABEL.classList.add("form-check-label", "flex-grow-1");
    LABEL.htmlFor = RADIO.id;
    LABEL.style.whiteSpace = "normal";
    LABEL.style.overflowWrap = "anywhere";
    LABEL.textContent = option.textContent ?? option.value;

    RADIO.addEventListener("change", () => {
        if (!RADIO.checked) {
            return;
        }

        select.value = RADIO.value;
        select.dispatchEvent(new Event("change", { bubbles: true }));
        onSelect();
    });

    ROW.append(RADIO, LABEL);
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

function updateButtonText(select: HTMLSelectElement, button: HTMLButtonElement, placeholder: string): void {
    const TEXT = button.querySelector<HTMLElement>("[data-searchable-radio-select-button-text]");
    if (TEXT === null) {
        throw new Error("Searchable radio select button text element was not found.");
    }

    const SELECTED_OPTION = select.selectedOptions.item(0);
    TEXT.textContent = SELECTED_OPTION?.textContent || placeholder;
}

function sanitizeIdPart(value: string): string {
    const SANITIZED = value.replace(/[^a-zA-Z0-9_-]/g, "_");
    return SANITIZED.length > 0
        ? SANITIZED
        : "empty";
}
