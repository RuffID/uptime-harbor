export interface HelpTooltipLink {
    text: string;
    href: string;
}

export type HelpTooltipTextPart = string | HelpTooltipLink;
export type HelpTooltipParagraph = string | HelpTooltipTextPart[];

export interface HelpTooltipOptions {
    title: string;
    text: string | HelpTooltipParagraph[];
}

interface ActiveHelpTooltip {
    trigger: HTMLElement;
    popup: HTMLElement;
    pinned: boolean;
}

const HOVER_OPEN_DELAY_MS = 500;
const HOVER_CLOSE_DELAY_MS = 150;

let activeHelpTooltip: ActiveHelpTooltip | null = null;
let hoverOpenTimerId: number | null = null;
let hoverCloseTimerId: number | null = null;

export function renderHelpTooltip(host: HTMLElement, options: HelpTooltipOptions): HTMLButtonElement {
    if (activeHelpTooltip && host.contains(activeHelpTooltip.trigger)) {
        closeActiveHelpTooltip();
    }
    host.replaceChildren();

    const TRIGGER = document.createElement("button");
    TRIGGER.type = "button";
    TRIGGER.classList.add("btn", "btn-sm", "btn-outline-secondary", "rounded-circle", "d-inline-flex", "align-items-center", "justify-content-center", "p-0");
    TRIGGER.style.width = "1.25rem";
    TRIGGER.style.height = "1.25rem";
    TRIGGER.style.lineHeight = "1";
    TRIGGER.style.transform = "translateY(-2px)";
    TRIGGER.setAttribute("aria-label", options.title);
    TRIGGER.setAttribute("aria-expanded", "false");
    TRIGGER.textContent = "?";

    bindHelpTooltip(TRIGGER, options);
    host.append(TRIGGER);

    return TRIGGER;
}

function bindHelpTooltip(trigger: HTMLElement, options: HelpTooltipOptions): void {
    trigger.addEventListener("focus", () => showHelpTooltip(trigger, options, false));
    trigger.addEventListener("blur", () => hideHoverHelpTooltip(trigger));
    trigger.addEventListener("mouseenter", () => {
        clearHoverCloseTimer();
        scheduleHoverHelpTooltip(trigger, options);
    });
    trigger.addEventListener("mouseleave", () => {
        clearHoverOpenTimer();
        scheduleHideHoverHelpTooltip(trigger);
    });
    trigger.addEventListener("click", EVENT => {
        EVENT.preventDefault();
        EVENT.stopPropagation();
        clearHoverOpenTimer();
        clearHoverCloseTimer();
        showHelpTooltip(trigger, options, true);
    });
}

function scheduleHoverHelpTooltip(trigger: HTMLElement, options: HelpTooltipOptions): void {
    clearHoverOpenTimer();

    hoverOpenTimerId = window.setTimeout(() => {
        hoverOpenTimerId = null;
        showHelpTooltip(trigger, options, false);
    }, HOVER_OPEN_DELAY_MS);
}

function clearHoverOpenTimer(): void {
    if (hoverOpenTimerId === null) {
        return;
    }

    window.clearTimeout(hoverOpenTimerId);
    hoverOpenTimerId = null;
}

function scheduleHideHoverHelpTooltip(trigger: HTMLElement): void {
    clearHoverCloseTimer();
    hoverCloseTimerId = window.setTimeout(() => {
        hoverCloseTimerId = null;
        hideHoverHelpTooltip(trigger);
    }, HOVER_CLOSE_DELAY_MS);
}

function clearHoverCloseTimer(): void {
    if (hoverCloseTimerId === null) {
        return;
    }

    window.clearTimeout(hoverCloseTimerId);
    hoverCloseTimerId = null;
}

function showHelpTooltip(trigger: HTMLElement, options: HelpTooltipOptions, pinned: boolean): void {
    clearHoverCloseTimer();
    if (activeHelpTooltip && activeHelpTooltip.trigger !== trigger) {
        closeActiveHelpTooltip();
    }

    if (!activeHelpTooltip) {
        activeHelpTooltip = {
            trigger: trigger,
            popup: createHelpTooltipPopup(options),
            pinned: pinned
        };

        document.body.append(activeHelpTooltip.popup);
        trigger.setAttribute("aria-expanded", "true");
        trigger.setAttribute("aria-describedby", activeHelpTooltip.popup.id);
        bindHelpTooltipPopupHover(activeHelpTooltip.popup);
    } else {
        activeHelpTooltip.pinned = activeHelpTooltip.pinned || pinned;
    }

    positionHelpTooltip(trigger, activeHelpTooltip.popup);
    window.addEventListener("resize", handleWindowResize);
    document.addEventListener("scroll", handleWindowResize, true);
    document.addEventListener("keydown", handleEscape);
    document.addEventListener("pointerdown", handleDocumentPointerDown, true);
}

function bindHelpTooltipPopupHover(popup: HTMLElement): void {
    popup.addEventListener("mouseenter", clearHoverCloseTimer);
    popup.addEventListener("mouseleave", () => {
        if (!activeHelpTooltip || activeHelpTooltip.popup !== popup || activeHelpTooltip.pinned) {
            return;
        }

        scheduleHideHoverHelpTooltip(activeHelpTooltip.trigger);
    });
}

function hideHoverHelpTooltip(trigger: HTMLElement): void {
    if (!activeHelpTooltip || activeHelpTooltip.trigger !== trigger || activeHelpTooltip.pinned) {
        return;
    }

    closeActiveHelpTooltip();
}

function createHelpTooltipPopup(options: HelpTooltipOptions): HTMLElement {
    const POPUP = document.createElement("div");
    POPUP.classList.add("position-fixed", "border", "rounded-3", "shadow", "bg-body", "text-body");
    POPUP.setAttribute("role", "tooltip");
    POPUP.id = "active-help-tooltip";
    POPUP.style.maxWidth = "320px";
    POPUP.style.minWidth = "240px";
    POPUP.style.overflowX = "hidden";
    POPUP.style.zIndex = "1080";

    const HEADER = document.createElement("div");
    HEADER.classList.add("bg-body-tertiary", "border-bottom", "fw-semibold", "px-3", "py-2");
    HEADER.textContent = options.title;

    const BODY = document.createElement("div");
    BODY.classList.add("px-3", "py-2");
    appendHelpTooltipBodyText(BODY, options.text);

    POPUP.append(HEADER, BODY);

    return POPUP;
}

function appendHelpTooltipBodyText(body: HTMLElement, text: string | HelpTooltipParagraph[]): void {
    if (typeof text === "string") {
        body.textContent = text;
        return;
    }

    body.classList.add("d-flex", "flex-column", "gap-2");

    for (const PARAGRAPH of text) {
        const LINE = document.createElement("div");
        appendHelpTooltipParagraph(LINE, PARAGRAPH);
        body.append(LINE);
    }
}

function appendHelpTooltipParagraph(line: HTMLElement, paragraph: HelpTooltipParagraph): void {
    if (typeof paragraph === "string") {
        line.textContent = paragraph;
        return;
    }

    for (const PART of paragraph) {
        if (typeof PART === "string") {
            line.append(document.createTextNode(PART));
            continue;
        }

        const LINK = document.createElement("a");
        LINK.classList.add("text-primary", "text-decoration-none");
        LINK.href = PART.href;
        LINK.textContent = PART.text;
        line.append(LINK);
    }
}

function positionHelpTooltip(trigger: HTMLElement, popup: HTMLElement): void {
    const TRIGGER_RECT = trigger.getBoundingClientRect();
    const VIEWPORT_PADDING = 8;
    const TOOLTIP_GAP = 8;
    const MAX_POPUP_HEIGHT = Math.max(0, window.innerHeight - VIEWPORT_PADDING * 2);
    popup.style.maxHeight = `${MAX_POPUP_HEIGHT}px`;
    popup.style.overflowY = "auto";
    const POPUP_RECT = popup.getBoundingClientRect();
    const SPACE_BELOW = window.innerHeight - TRIGGER_RECT.bottom - TOOLTIP_GAP - VIEWPORT_PADDING;
    const SPACE_ABOVE = TRIGGER_RECT.top - TOOLTIP_GAP - VIEWPORT_PADDING;
    let top: number;
    let left = TRIGGER_RECT.left;

    if (POPUP_RECT.height <= SPACE_BELOW) {
        top = TRIGGER_RECT.bottom + TOOLTIP_GAP;
    } else if (POPUP_RECT.height <= SPACE_ABOVE) {
        top = TRIGGER_RECT.top - POPUP_RECT.height - TOOLTIP_GAP;
    } else {
        top = Math.min(
            Math.max(VIEWPORT_PADDING, TRIGGER_RECT.bottom + TOOLTIP_GAP),
            window.innerHeight - VIEWPORT_PADDING - POPUP_RECT.height
        );

        const SPACE_RIGHT = window.innerWidth - TRIGGER_RECT.right - TOOLTIP_GAP - VIEWPORT_PADDING;
        const SPACE_LEFT = TRIGGER_RECT.left - TOOLTIP_GAP - VIEWPORT_PADDING;
        if (POPUP_RECT.width <= SPACE_RIGHT) {
            left = TRIGGER_RECT.right + TOOLTIP_GAP;
        } else if (POPUP_RECT.width <= SPACE_LEFT) {
            left = TRIGGER_RECT.left - POPUP_RECT.width - TOOLTIP_GAP;
        }
    }

    if (left + POPUP_RECT.width > window.innerWidth - VIEWPORT_PADDING) {
        left = window.innerWidth - POPUP_RECT.width - VIEWPORT_PADDING;
    }

    if (left < VIEWPORT_PADDING) {
        left = VIEWPORT_PADDING;
    }

    popup.style.left = `${left}px`;
    popup.style.top = `${top}px`;
}

function handleWindowResize(): void {
    if (!activeHelpTooltip) {
        return;
    }

    if (!activeHelpTooltip.trigger.isConnected) {
        closeActiveHelpTooltip();
        return;
    }
    positionHelpTooltip(activeHelpTooltip.trigger, activeHelpTooltip.popup);
}

function handleEscape(event: KeyboardEvent): void {
    if (event.key === "Escape") {
        closeActiveHelpTooltip();
    }
}

function handleDocumentPointerDown(event: PointerEvent): void {
    if (!activeHelpTooltip || !activeHelpTooltip.pinned) {
        return;
    }

    const TARGET = event.target;
    if (!(TARGET instanceof Node)) {
        return;
    }

    if (activeHelpTooltip.popup.contains(TARGET) || activeHelpTooltip.trigger.contains(TARGET)) {
        return;
    }

    closeActiveHelpTooltip();
}

function closeActiveHelpTooltip(): void {
    if (!activeHelpTooltip) {
        return;
    }

    clearHoverOpenTimer();
    clearHoverCloseTimer();
    activeHelpTooltip.trigger.setAttribute("aria-expanded", "false");
    activeHelpTooltip.trigger.removeAttribute("aria-describedby");
    activeHelpTooltip.popup.remove();
    activeHelpTooltip = null;
    window.removeEventListener("resize", handleWindowResize);
    document.removeEventListener("scroll", handleWindowResize, true);
    document.removeEventListener("keydown", handleEscape);
    document.removeEventListener("pointerdown", handleDocumentPointerDown, true);
}
