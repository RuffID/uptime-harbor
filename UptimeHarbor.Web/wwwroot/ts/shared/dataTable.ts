import { renderHelpTooltip } from "./helpTooltip.js";
import { initTableColumnResizing } from "./tableColumnResizing.js";

export interface TableColumn {
    key: string;
    title: string;
    width?: number;
    help?: string;
}

export function createDataTable(host: HTMLElement, key: string, title: string, columns: TableColumn[]) {
    host.classList.add("w-100", "mw-100");
    host.style.minWidth = "0";
    const scroll = document.createElement("div");
    scroll.classList.add("overflow-auto", "w-100", "mw-100", "border", "border-secondary-subtle", "rounded");
    scroll.tabIndex = 0;
    scroll.setAttribute("role", "region");
    scroll.setAttribute("aria-label", title);
    scroll.dataset.tableScroll = key;

    const table = document.createElement("table");
    table.classList.add("table", "table-sm", "table-bordered", "table-hover", "align-middle", "mb-0");
    table.dataset.tableKey = key;
    table.style.tableLayout = "fixed";
    table.style.minWidth = "100%";
    table.style.width = "100%";
    const totalWidth = columns.reduce((sum, column) => sum + (column.width ?? 180), 0);
    const caption = table.createCaption();
    caption.classList.add("visually-hidden");
    caption.textContent = title;
    const headers = table.createTHead().insertRow();
    for (const column of columns) {
        const cell = document.createElement("th");
        cell.scope = "col";
        cell.dataset.columnKey = column.key;
        cell.classList.add("sticky-top", "bg-body-tertiary", "px-3", "py-2");
        cell.dataset.columnDefaultWidth = String(column.width ?? 180);
        cell.style.width = `${(column.width ?? 180) / totalWidth * 100}%`;
        cell.append(document.createTextNode(column.title));
        if (column.help) {
            const help = document.createElement("span");
            help.classList.add("ms-2");
            renderHelpTooltip(help, { title: column.title, text: column.help });
            cell.append(help);
        }
        headers.append(cell);
    }
    const body = table.createTBody();
    scroll.append(table);
    host.replaceChildren(scroll);
    const resizing = initTableColumnResizing(table, { pageKey: window.location.pathname });

    function refresh(): void {
        const top = scroll.getBoundingClientRect().top;
        scroll.style.maxHeight = `${Math.max(160, window.innerHeight - Math.max(0, top) - 48)}px`;
        resizing.refresh();
    }

    function row(values: Array<string | Node>, selected = false): HTMLTableRowElement {
        if (values.length !== columns.length) {
            throw new Error(`Число ячеек таблицы «${key}» не совпадает с числом колонок.`);
        }
        const row = document.createElement("tr");
        row.classList.toggle("table-active", selected);
        for (const [index, value] of values.entries()) {
            const cell = row.insertCell();
            cell.dataset.columnKey = columns[index].key;
            cell.classList.add("px-3", "py-2", "text-break");
            cell.append(value);
        }
        return row;
    }

    window.addEventListener("resize", refresh);
    const layoutObserver = new ResizeObserver(refresh);
    layoutObserver.observe(document.body);
    refresh();
    return { body, row, refresh };
}

export function createTableAction(label: string): HTMLButtonElement {
    const button = document.createElement("button");
    button.type = "button";
    button.classList.add("btn", "btn-sm", "btn-outline-secondary");
    button.textContent = label;
    return button;
}

export function matchesSearch(search: string, ...values: Array<string | null>): boolean {
    const query = search.trim().toLocaleLowerCase("ru-RU");
    return values.some(value => value?.toLocaleLowerCase("ru-RU").includes(query));
}
