import type { EntityKind, Settings, Operation, Observer } from "./clusterModels.js";

export const kindNames: Record<EntityKind, string> = {
    Server: "Серверы", Container: "Контейнеры", Chat: "Telegram-чаты", Binding: "Привязки целей к чатам",
    NodeChatRoute: "Маршруты узлов в чаты", Account: "Администраторы"
};
const actions = new WeakMap<HTMLButtonElement, () => void>();
export function delegateActions(root: HTMLElement): void {
    root.addEventListener("click", event => {
        if (!(event.target instanceof Element)) return;
        const target = event.target.closest<HTMLButtonElement>("button[data-cluster-action]");
        if (!target || !root.contains(target) || target.disabled) return;
        const action = actions.get(target);
        if (!action) throw new Error("Неизвестное действие редактора сетки.");
        action();
    });
}
export function element<K extends keyof HTMLElementTagNameMap>(tag: K, text = "", classes = ""): HTMLElementTagNameMap[K] {
    const node = document.createElement(tag);
    node.textContent = text;
    if (classes) node.className = classes;
    return node;
}
export function button(text: string, run: () => void, danger = false): HTMLButtonElement {
    const node = element("button", text, `btn btn-sm ${danger ? "btn-outline-danger" : "btn-outline-primary"}`);
    node.type = "button";
    node.dataset.clusterAction = "run";
    actions.set(node, run);
    return node;
}
export function date(value: string | null): string { return value === null ? "неизвестно" : new Date(value).toLocaleString(); }
export function metadataDiffer(observers: Observer[]): boolean {
    const known = observers.filter(observer => observer.metadataAtUtc !== null);
    return new Set(known.map(observer => JSON.stringify([observer.endpointName, observer.startedAtUtc]))).size > 1;
}
export function operationText(value: Operation): string {
    const names: Record<string, string> = {
        LocalPending: "Сохранено локально. Ожидает синхронизации и принятия.",
        Committed: "Команда принята.", Conflict: "Конфликт. Первая принятая версия действует.",
        Rejected: "Отклонено.", Cancelled: "Отмена принята.", Replaced: "Замена принята."
    };
    let text = `${value.operationId}: ${names[value.status] ?? "Результат неизвестен."}`;
    if (value.message) text += ` ${value.message}`;
    if (value.appliedRevision !== null) text += ` Ревизия ${value.appliedRevision}.`;
    if (value.replacementResult) text += ` Результат новой команды: ${operationText(value.replacementResult)}`;
    return text;
}
export function settingsText(value: Settings | null, kind: EntityKind): string {
    if (value === null) return "Нет варианта настроек (удаление или команда без значения).";
    if (kind === "Account") return `Логин: ${value.name ?? "—"}; ${value.enabled ? "включён" : "отключён"}. Пароль и служебные реквизиты не отображаются.`;
    if (kind === "Chat") return `${value.name ?? "—"}; Chat ID: ${value.telegramChatId}`;
    if (kind === "Binding") return `Цель: ${value.parentId}; чат: ${value.chatId}; тип: ${value.enabled ? "сервер" : "контейнер"}`;
    if (kind === "NodeChatRoute") return `NodeId: ${value.parentId}; чат: ${value.chatId}; ${value.enabled ? "включён" : "отключён"}`;
    return `${value.name ?? "—"}; ${value.endpoint ?? "—"}; ${value.enabled ? "включён" : "отключён"}; сервер: ${value.parentId ?? "нет"}; observers: ${value.observerNodes.join(", ") || "нет"}; интервал ${value.intervalSeconds} с, timeout ${value.timeoutSeconds} с, порог ${value.failureThreshold}. ${value.description ?? ""}`;
}
