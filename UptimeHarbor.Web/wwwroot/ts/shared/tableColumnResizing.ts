const STORAGE_PREFIX = "uptimeHarborTableColumnWidth";
const DEFAULT_MIN_WIDTH = 96;

export interface TableColumnResizingOptions {
    pageKey: string;
    synchronizeWidths?: boolean;
}

interface SynchronizedColumnWidths {
    widths: Map<string, number>;
    loadedKeys: Set<string>;
    listeners: Set<(columnKey: string, width: number) => void>;
}

const synchronizedWidths = new Map<string, SynchronizedColumnWidths>();

export interface TableColumnResizingController {
    refresh(): void;
    destroy(): void;
}

export function initTableColumnResizing(
    table: HTMLTableElement,
    options: TableColumnResizingOptions
): TableColumnResizingController {
    const PAGE_KEY = requireStableKey(options.pageKey, "страницы");
    const TABLE_KEY = requireStableKey(table.dataset.tableKey, "таблицы");
    const GROUP_KEY = buildStorageKey(PAGE_KEY, TABLE_KEY, "");
    let group: SynchronizedColumnWidths | undefined;
    if (options.synchronizeWidths) {
        group = synchronizedWidths.get(GROUP_KEY);
        if (group === undefined) {
            group = { widths: new Map(), loadedKeys: new Set(), listeners: new Set() };
        }
    }
    const widths = group?.widths ?? new Map<string, number>();
    const minimumWidths = new Map<string, number>();
    const loadedKeys = group?.loadedKeys ?? new Set<string>();
    let tableWidthAdjustment = 0;

    function freezeRenderedWidths(): void {
        const headers = Array.from(table.tHead!.querySelectorAll<HTMLTableCellElement>("th[data-column-key]"));
        const measured = headers.map(header => ({
            key: requireStableKey(header.dataset.columnKey, "колонки"),
            width: header.getBoundingClientRect().width
        }));
        tableWidthAdjustment = table.getBoundingClientRect().width - measured.reduce((sum, column) => sum + column.width, 0);
        table.style.minWidth = "0";
        for (const column of measured) widths.set(column.key, column.width);
        for (const column of measured) updateColumnWidth(column.key, column.width);
    }

    function persistWidths(): void {
        for (const header of table.tHead!.querySelectorAll<HTMLTableCellElement>("th[data-column-key]")) {
            const key = requireStableKey(header.dataset.columnKey, "колонки");
            const width = widths.get(key);
            if (header.dataset.columnPersistWidth !== "false" && width !== undefined) {
                localStorage.setItem(buildStorageKey(PAGE_KEY, TABLE_KEY, key), String(width));
            }
        }
    }

    function updateColumnWidth(columnKey: string, width: number): void {
        widths.set(columnKey, width);
        if (group === undefined) {
            applyColumnWidth(columnKey, width);
            return;
        }
        for (const LISTENER of group.listeners) {
            LISTENER(columnKey, width);
        }
    }

    function refresh(): void {
        const HEAD = table.tHead;
        if (HEAD === null) {
            return;
        }

        const IS_VISIBLE = table.getClientRects().length > 0;
        const HEADERS = Array.from(HEAD.querySelectorAll<HTMLTableCellElement>("th[data-column-key]"));
        for (const HEADER of HEADERS) {
            const COLUMN_KEY = requireStableKey(HEADER.dataset.columnKey, "колонки");
            loadStoredWidth(COLUMN_KEY, HEADER.dataset.columnPersistWidth !== "false");
            if (IS_VISIBLE && !minimumWidths.has(COLUMN_KEY)) {
                minimumWidths.set(COLUMN_KEY, Math.min(readMinWidth(HEADER), HEADER.getBoundingClientRect().width));
            }
            const WIDTH = widths.get(COLUMN_KEY);
            if (IS_VISIBLE && WIDTH !== undefined) {
                applyColumnWidth(COLUMN_KEY, WIDTH);
            }
            ensureResizeHandle(HEADER, COLUMN_KEY);
        }
    }

    function loadStoredWidth(columnKey: string, persists: boolean): void {
        if (!persists || loadedKeys.has(columnKey)) {
            return;
        }

        const STORED_VALUE = localStorage.getItem(buildStorageKey(PAGE_KEY, TABLE_KEY, columnKey));
        if (STORED_VALUE === null) {
            loadedKeys.add(columnKey);
            return;
        }

        const WIDTH = Number(STORED_VALUE);
        if (!Number.isFinite(WIDTH) || WIDTH <= 0) {
            throw new Error(`Некорректная сохраненная ширина колонки «${columnKey}».`);
        }
        widths.set(columnKey, WIDTH);
        loadedKeys.add(columnKey);
    }

    function ensureResizeHandle(header: HTMLTableCellElement, columnKey: string): void {
        if (header.querySelector<HTMLElement>("[data-column-resize-handle]") !== null) {
            return;
        }

        if (!header.classList.contains("sticky-top")) {
            header.classList.add("position-relative");
        }
        const HANDLE = document.createElement("button");
        HANDLE.type = "button";
        HANDLE.classList.add("position-absolute", "top-0", "bottom-0", "end-0", "border-0", "bg-transparent", "px-1", "py-0", "z-2");
        HANDLE.dataset.columnResizeHandle = "";
        const COLUMN_NAME = header.textContent?.trim() || columnKey;
        HANDLE.ariaLabel = `Изменить ширину колонки «${COLUMN_NAME}»`;
        HANDLE.title = HANDLE.ariaLabel;
        HANDLE.style.cursor = "col-resize";
        HANDLE.style.touchAction = "none";
        HANDLE.addEventListener("keydown", EVENT => {
            if (EVENT.key !== "ArrowLeft" && EVENT.key !== "ArrowRight") {
                return;
            }
            EVENT.preventDefault();
            freezeRenderedWidths();
            const WIDTH = Math.max(readMinWidth(header), Math.round(header.getBoundingClientRect().width)
                + (EVENT.key === "ArrowRight" ? 16 : -16));
            updateColumnWidth(columnKey, WIDTH);
            if (header.dataset.columnPersistWidth !== "false") {
                persistWidths();
            }
        });
        HANDLE.addEventListener("pointerenter", () => setResizeHandleHighlighted(HANDLE, true));
        HANDLE.addEventListener("pointerleave", EVENT => {
            if (!HANDLE.hasPointerCapture(EVENT.pointerId)) {
                setResizeHandleHighlighted(HANDLE, document.activeElement === HANDLE);
            }
        });
        HANDLE.addEventListener("focus", () => setResizeHandleHighlighted(HANDLE, true));
        HANDLE.addEventListener("blur", () => setResizeHandleHighlighted(HANDLE, HANDLE.matches(":hover")));
        HANDLE.addEventListener("pointerdown", EVENT => beginResize(EVENT, header, columnKey, HANDLE));
        header.append(HANDLE);
    }

    function beginResize(
        event: PointerEvent,
        header: HTMLTableCellElement,
        columnKey: string,
        handle: HTMLButtonElement
    ): void {
        if (event.button !== 0) {
            return;
        }

        event.preventDefault();
        event.stopPropagation();
        freezeRenderedWidths();
        const START_X = event.clientX;
        const START_WIDTH = header.getBoundingClientRect().width;
        const MIN_WIDTH = minimumWidths.get(columnKey) ?? Math.min(readMinWidth(header), START_WIDTH);
        minimumWidths.set(columnKey, MIN_WIDTH);
        const PERSISTS = header.dataset.columnPersistWidth !== "false";
        handle.setPointerCapture(event.pointerId);
        setResizeHandleHighlighted(handle, true);
        document.documentElement.classList.add("user-select-none");

        const resize = (moveEvent: PointerEvent): void => {
            if (moveEvent.pointerId !== event.pointerId) {
                return;
            }
            const WIDTH = Math.max(MIN_WIDTH, START_WIDTH + moveEvent.clientX - START_X);
            updateColumnWidth(columnKey, WIDTH);
        };

        const finish = (endEvent: PointerEvent): void => {
            if (endEvent.pointerId !== event.pointerId) {
                return;
            }
            handle.removeEventListener("pointermove", resize);
            handle.removeEventListener("pointerup", finish);
            handle.removeEventListener("pointercancel", cancel);
            handle.removeEventListener("lostpointercapture", cancel);
            document.documentElement.classList.remove("user-select-none");
            setResizeHandleHighlighted(handle, handle.matches(":hover") || document.activeElement === handle);
            const WIDTH = widths.get(columnKey);
            if (PERSISTS && WIDTH !== undefined) {
                persistWidths();
            }
        };

        const cancel = (cancelEvent: PointerEvent): void => {
            if (cancelEvent.pointerId !== event.pointerId) {
                return;
            }
            updateColumnWidth(columnKey, START_WIDTH);
            handle.removeEventListener("pointermove", resize);
            handle.removeEventListener("pointerup", finish);
            handle.removeEventListener("pointercancel", cancel);
            handle.removeEventListener("lostpointercapture", cancel);
            document.documentElement.classList.remove("user-select-none");
            setResizeHandleHighlighted(handle, handle.matches(":hover") || document.activeElement === handle);
        };

        handle.addEventListener("pointermove", resize);
        handle.addEventListener("pointerup", finish);
        handle.addEventListener("pointercancel", cancel);
        handle.addEventListener("lostpointercapture", cancel);
    }

    function applyColumnWidth(columnKey: string, width: number): void {
        const WIDTH = `${width}px`;
        const CELLS = table.querySelectorAll<HTMLElement>("[data-column-key]");
        for (const CELL of Array.from(CELLS)) {
            if (CELL.closest("table") !== table || CELL.dataset.columnKey !== columnKey) {
                continue;
            }
            CELL.style.width = WIDTH;
            CELL.style.minWidth = WIDTH;
            CELL.style.maxWidth = WIDTH;
        }
        const HEADERS = Array.from(table.tHead?.querySelectorAll<HTMLElement>("th[data-column-key]") ?? []);
        table.style.width = `${tableWidthAdjustment + HEADERS.reduce((sum, cell) => sum + Number.parseFloat(cell.style.width), 0)}px`;
    }

    refresh();
    if (group !== undefined) {
        group.listeners.add(applyColumnWidth);
        synchronizedWidths.set(GROUP_KEY, group);
    }
    const OBSERVER = new MutationObserver(() => queueMicrotask(refresh));
    OBSERVER.observe(table, { childList: true, subtree: true });
    const RESIZE_OBSERVER = new ResizeObserver(refresh);
    RESIZE_OBSERVER.observe(table);

    function destroy(): void {
        OBSERVER.disconnect();
        RESIZE_OBSERVER.disconnect();
        group?.listeners.delete(applyColumnWidth);
        if (group?.listeners.size === 0) {
            synchronizedWidths.delete(GROUP_KEY);
        }
    }

    return { refresh, destroy };
}

function setResizeHandleHighlighted(handle: HTMLButtonElement, isHighlighted: boolean): void {
    handle.classList.toggle("border-end", isHighlighted);
    handle.classList.toggle("border-3", isHighlighted);
    handle.classList.toggle("border-primary", isHighlighted);
}

function readMinWidth(header: HTMLTableCellElement): number {
    const VALUE = header.dataset.columnMinWidth;
    if (VALUE === undefined) {
        return DEFAULT_MIN_WIDTH;
    }

    const WIDTH = Number(VALUE);
    if (!Number.isFinite(WIDTH) || WIDTH <= 0) {
        throw new Error("Минимальная ширина колонки должна быть положительным числом.");
    }
    return WIDTH;
}

function buildStorageKey(pageKey: string, tableKey: string, columnKey: string): string {
    return [STORAGE_PREFIX, pageKey, tableKey, columnKey].map(encodeURIComponent).join(":");
}

function requireStableKey(value: string | undefined, entityName: string): string {
    const KEY = value?.trim();
    if (!KEY) {
        throw new Error(`Не задан стабильный ключ ${entityName} для сохранения ширины колонки.`);
    }
    return KEY;
}
