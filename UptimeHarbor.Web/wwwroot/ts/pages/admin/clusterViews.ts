import { createDataTable } from "../../shared/dataTable.js";
import { element, button, date, kindNames, settingsText, operationText, metadataDiffer } from "./clusterDom.js";
import type { Snapshot, Pending, Diagnostic, Entity, EntityKind, Conflict } from "./clusterModels.js";

interface Actions {
    edit(kind: EntityKind, entity: Entity | null, action?: string, pending?: Pending, conflict?: Conflict, useCurrent?: boolean): void;
    cancel(pending: Pending): void;
    find(id: string): void;
}
export class ClusterViews {
    private accepted;
    private pending;
    private targets;
    private observers;
    private deliveries;
    private nodes;
    private conflicts: HTMLElement;
    private summary: HTMLElement;
    constructor(root: HTMLElement, private actions: Actions) {
        const host = (key: string): HTMLElement => {
            const node = root.querySelector<HTMLElement>(`[data-cluster-${key}]`);
            if (!node) throw new Error(`Отсутствует контейнер ${key}.`);
            return node;
        };
        const table = (key: string, title: string, columns: string[]) => createDataTable(host(key), `cluster-${key}`, title,
            columns.map((text, index) => ({ key: String(index), title: text, width: index === 0 ? 270 : 210 })));
        this.accepted = table("accepted", "Принятая конфигурация", ["Объект / ID", "Ревизия", "Настройки", "Действия"]);
        this.pending = table("pending", "Локальные Pending", ["Операция / сущность", "Состояние", "Предложенные настройки", "Действия / результат"]);
        this.targets = table("targets", "Доступность и наблюдение", ["Цель", "Availability", "Coverage", "Принятая оценка"]);
        this.observers = table("observers", "Наблюдения по узлам", ["Цель / observer", "Результат и свежесть", "Metadata по observer", "Uptime по metadata"]);
        this.deliveries = table("deliveries", "Доставки", ["Чат / intent", "Событие", "Состояние", "Реальные попытки / ошибка"]);
        this.nodes = table("nodes", "Узлы", ["NodeId / роль", "Связь / последний контакт", "Локальная БД", "Applied / lag"]);
        this.conflicts = host("conflicts"); this.summary = host("diagnostic-summary");
    }

    render(snapshot: Snapshot, pending: Pending[], diagnostic: Diagnostic, kind: EntityKind): void {
        const acceptedRows = snapshot.entities.filter(item => item.kind === kind && !item.deleted).map(item => {
            const actions = element("div", "", "d-flex flex-wrap gap-2");
            actions.append(button("Изменить", () => this.actions.edit(kind, item)));
            if (kind === "Account") {
                actions.append(button("Пароль", () => this.actions.edit(kind, item, "Password")));
                const self = item.id === snapshot.currentUserId;
                const onlyActive = item.settings.enabled && snapshot.entities.filter(value => value.kind === "Account" && !value.deleted && value.settings.enabled).length <= 1;
                const enabled = button("Включение / отключение", () => this.actions.edit(kind, item, "SetEnabled"));
                enabled.disabled = self || onlyActive;
                const remove = button("Удалить глобально", () => this.actions.edit(kind, item, "Delete"), true);
                remove.disabled = self || onlyActive;
                actions.append(enabled, remove);
            }
            else actions.append(button("Удалить глобально", () => this.actions.edit(kind, item, "Delete"), true));
            if (kind === "Chat") actions.append(button("Тест в очередь", () => this.actions.edit(kind, item, "TestDelivery")));
            return this.accepted.row([`${item.settings.name ?? kindNames[kind]} · ${item.id}`, String(item.revision), settingsText(item.settings, kind), actions]);
        });
        this.accepted.body.replaceChildren(...acceptedRows); this.accepted.refresh();
        this.renderPending(snapshot, pending);
        this.renderConflicts(snapshot);
        const name = (id: string): string => `${snapshot.entities.find(item => item.id === id)?.settings.name ?? (snapshot.nodes.includes(id) ? "Узел" : "Цель")} · ${id}`;
        this.targets.body.replaceChildren(...snapshot.targets.map(target => this.targets.row([
            name(target.id), target.availability, target.coverage,
            `${date(target.evaluatedAtUtc)}${target.evaluationStale ? " · устарела / отсутствует" : ""}${metadataDiffer(target.observers) ? " · metadata различаются, см. строки observers" : ""}`
        ])));
        this.targets.refresh();
        this.observers.body.replaceChildren(...snapshot.targets.flatMap(target => target.observers.map(observer => this.observers.row([
            `${name(target.id)} / ${observer.nodeId}`,
            `${observer.status} · ${date(observer.checkedAtUtc)} · ${observer.failure ?? ""}`,
            `${observer.endpointName ?? "metadata отсутствуют"} · ${date(observer.metadataAtUtc)}`,
            observer.uptimeSeconds === null ? "неизвестно" : `${observer.uptimeSeconds} с от запуска ${date(observer.startedAtUtc)} (по metadata этого observer)`
        ]))));
        this.observers.refresh();
        this.deliveries.body.replaceChildren(...snapshot.deliveries.map(delivery => this.deliveries.row([
            `${delivery.chatId} / ${delivery.id}`, `${delivery.kind} · ${delivery.incidentId ?? "—"}`,
            `${delivery.status} · ${date(delivery.completedAtUtc)}`, `${delivery.attempts} / 5 · ${delivery.failure ?? "—"}`
        ])));
        this.deliveries.refresh();
        this.renderDiagnostics(diagnostic);
    }

    private renderPending(snapshot: Snapshot, rows: Pending[]): void {
        this.pending.body.replaceChildren(...rows.map(row => {
            const actions = element("div", "", "d-flex flex-wrap gap-2");
            const current = snapshot.entities.find(item => item.id === row.entityId) ?? null;
            if (row.result) actions.append(element("p", operationText(row.result), "small mb-0"));
            else actions.append(button("Проверить принятый результат", () => this.actions.find(row.operationId)));
            const editable = !row.result && row.state !== "Resolved" && !row.requestedControlId && (row.action === "Upsert" || row.action === "Delete");
            if (editable && row.kind !== "Node") {
                const kind = row.kind;
                actions.append(button(row.state === "NeedsUserConfirmation" ? "Сверить и подтвердить новую заявку" : "Редактировать заявку",
                    () => this.actions.edit(kind, current, row.action, row)));
                actions.append(button("Запросить отмену", () => this.actions.cancel(row), true));
            }
            if (row.kind === "Node" && !row.result && row.state !== "Resolved" && !row.requestedControlId && row.action !== "Cancel" && row.action !== "Replace") {
                actions.append(button("Запросить отмену", () => this.actions.cancel(row), true));
                if (row.state === "NeedsUserConfirmation") actions.append(element("p", "Для подтверждения нужна новая эксплуатационная заявка с актуальной ревизией и явным ConfirmRestored.", "small"));
            }
            if (row.requestedControlId) actions.append(button(`Результат control ${row.requestedControlId}`, () => this.actions.find(row.requestedControlId!)));
            return this.pending.row([`${row.operationId} / ${row.entityId} · ${row.action} · base ${row.baseRevision}`,
                `${row.state} · ${date(row.createdAtUtc)}${row.state === "NeedsUserConfirmation" ? " · удержано после восстановления" : ""}`,
                row.kind === "Node" ? `Управление сеткой: ${row.action}` : settingsText(row.settings, row.kind), actions]);
        }));
        this.pending.refresh();
    }

    private renderConflicts(snapshot: Snapshot): void {
        this.conflicts.replaceChildren(...snapshot.conflicts.map(conflict => {
            const details = element("details", "", "border rounded p-3 mb-2");
            details.append(element("summary", `${kindNames[conflict.kind]} · ${conflict.entityId} · ${conflict.resolvedAtUtc ? "решён" : "конфликт"}`));
            details.append(element("p", `Первая принятая версия остаётся действующей. Base ${conflict.baseRevision}; текущая ревизия ${conflict.currentRevision}.`, "small mt-2"));
            const variant = (title: string, value: Entity | null): HTMLElement => element("p", `${title}: ${value?.deleted ? "удалено; " : ""}${settingsText(value?.settings ?? null, conflict.kind)}`, "small");
            details.append(variant("Исходная", conflict.baseline), variant("Первая принятая", conflict.firstAccepted),
                element("p", `Предложенная (${conflict.action}${conflict.accountAction ? ` / ${conflict.accountAction}` : ""}): ${settingsText(conflict.proposed, conflict.kind)}`, "small"),
                variant("Текущая", conflict.current));
            if (!conflict.resolvedAtUtc) {
                const actions = element("div", "", "d-flex flex-wrap gap-2");
                actions.append(button("Подготовить предложенный вариант", () => this.actions.edit(conflict.kind, conflict.current, conflict.action, undefined, conflict)));
                if (conflict.current && !conflict.current.deleted) actions.append(button("Подготовить текущий вариант", () => this.actions.edit(conflict.kind, conflict.current, "Upsert", undefined, conflict, true)));
                details.append(actions);
            }
            else details.append(element("p", `Решение: ${conflict.resolutionOperationId} · ${date(conflict.resolvedAtUtc)}`, "small"));
            return details;
        }));
    }

    private renderDiagnostics(value: Diagnostic): void {
        this.summary.replaceChildren(
            element("p", `Term: ${value.term ?? "неизвестен"}; membership: ${value.membershipVersion ?? "неизвестен"}; эксплуатационная ревизия: ${value.operationsRevision}. Maintenance: ${value.maintenanceId}.`),
            element("p", `Конфликты: ${value.conflicts}; terminal delivery errors: ${value.terminalDeliveryErrors}. Последняя успешная синхронизация команды: ${date(value.lastSuccessfulSynchronizationAtUtc)}. Возраст проекции: ${value.projectionAgeSeconds ?? "неизвестен"} с.`),
            element("p", `На ${date(value.atUtc)}. Текущий узел: ${value.nodeId}. Роль: ${value.role}. Главный: ${value.leaderNodeId ?? "неизвестен"}. Majority: ${value.majority} (${value.requiredMajority} из ${value.votingNodes}).`),
            element("p", `Локальная БД: ${value.localDatabase}. Applied: ${value.appliedIndex}; commit: ${value.committedIndex ?? "неизвестно"}; lag: ${value.lag ?? "неизвестно"}. Pending: ${value.pending}; удержано: ${value.heldPending}; доставка backlog: ${value.deliveryBacklog}.`),
            element("p", `Telegram readiness: ${value.telegram}; причина: ${value.telegramReason}; возраст проверки: ${value.telegramAgeSeconds === null ? "неизвестен" : `${Math.floor(value.telegramAgeSeconds)} с`}. Ошибка отдельного чата показана в журнале доставок и не означает потерю coverage.`)
        );
        this.nodes.body.replaceChildren(...value.nodes.map(node => this.nodes.row([
            `${node.nodeId} / ${node.role}`, `${node.connection} · ${date(node.lastContactAtUtc)} · возраст ${node.ageSeconds === null ? "неизвестен" : `${Math.floor(node.ageSeconds)} с`}`,
            node.database, `${node.appliedIndex ?? "неизвестно"} / ${node.lag ?? "неизвестно"}`
        ])));
        this.nodes.refresh();
    }

    clear(): void {
        for (const table of [this.accepted, this.pending, this.targets, this.observers, this.deliveries, this.nodes]) table.body.replaceChildren();
        this.conflicts.replaceChildren(); this.summary.replaceChildren();
    }
}
