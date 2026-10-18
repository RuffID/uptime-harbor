import { createDataTable, matchesSearch } from "../../shared/dataTable.js";
import { renderHelpTooltip } from "../../shared/helpTooltip.js";
import {
    ApiError,
    sendJsonRequest,
    ServiceResult,
    ServiceResultWithData,
    unwrapServiceResult,
    unwrapServiceSuccess
} from "../../shared/http.js";

type MonitoringObjectKind = 1 | 2;

interface TelegramChat {
    id: string;
    telegramChatId: number;
    name: string;
}

interface NotificationBinding {
    id: string;
    telegramChatId: string;
    serverId: string | null;
    containerId: string | null;
}

interface NotificationTarget {
    id: string;
    kind: MonitoringObjectKind;
    serverId: string | null;
    displayName: string;
}

interface NotificationSettings {
    chats: TelegramChat[];
    bindings: NotificationBinding[];
    targets: NotificationTarget[];
}

interface PageState extends NotificationSettings {
    selectedChatId: string | null;
    saving: boolean;
}

interface BootstrapModalInstance {
    show(): void;
    hide(): void;
}

interface BootstrapWindow extends Window {
    bootstrap?: { Modal: { getOrCreateInstance(element: Element): BootstrapModalInstance } };
}

const state: PageState = {
    chats: [],
    bindings: [],
    targets: [],
    selectedChatId: null,
    saving: false
};

const loading = requireElement<HTMLElement>("[data-loading]");
const content = requireElement<HTMLElement>("[data-content]");
const pageError = requireElement<HTMLElement>("[data-page-error]");
const chatList = requireElement<HTMLElement>("[data-chat-list]");
const chatEmpty = requireElement<HTMLElement>("[data-chat-empty]");
const page = requireElement<HTMLElement>("[data-notifications-page]");
const chatModal = requireElement<HTMLElement>("[data-chat-modal]");
const chatForm = requireElement<HTMLFormElement>("[data-chat-form]");
const chatFormTitle = requireElement<HTMLElement>("[data-chat-form-title]");
const chatIdInput = requireElement<HTMLInputElement>("[data-chat-id]");
const chatNameInput = requireElement<HTMLInputElement>("[data-chat-name]");
const chatGeneralError = requireElement<HTMLElement>("[data-chat-general-error]");
const bindingModal = requireElement<HTMLElement>("[data-binding-modal]");
const bindingForm = requireElement<HTMLFormElement>("[data-binding-form]");
const bindingChat = requireElement<HTMLSelectElement>("[data-binding-chat]");
const bindingKind = requireElement<HTMLSelectElement>("[data-binding-kind]");
const bindingObject = requireElement<HTMLSelectElement>("[data-binding-object]");
const bindingGeneralError = requireElement<HTMLElement>("[data-binding-general-error]");
const newBindingButton = requireElement<HTMLButtonElement>('[data-action="new-binding"]');
const chatSearch = requireElement<HTMLInputElement>("[data-chat-search]");
let chatsTable: ReturnType<typeof createDataTable>;

document.addEventListener("DOMContentLoaded", () => {
    void initNotificationsPage();
});

async function initNotificationsPage(): Promise<void> {
    chatsTable = createDataTable(chatList, "telegram", "Чаты и привязки Telegram", [
        { key: "chat", title: "Чат", width: 220 },
        { key: "chatId", title: "ID чата", width: 180, help: "Числовой идентификатор чата Telegram, в который отправляются сообщения. Это не имя пользователя и не token бота." },
        { key: "kind", title: "Тип объекта", width: 160 },
        { key: "object", title: "Объект", width: 240, help: "Объект мониторинга, уведомления которого направляются в этот чат. Для каждой привязки показана отдельная строка." },
        { key: "bindingActions", title: "Привязка", width: 180 },
        { key: "chatActions", title: "Действия с чатом", width: 370, help: "«Отправить тест» отправляет реальное тестовое сообщение в Telegram. Удаление чата удаляет и все его привязки." }
    ]);
    renderHelpTooltip(requireElement<HTMLElement>("[data-binding-help]"), {
        title: "Привязки уведомлений",
        text: "Привязка направляет уведомления выбранного объекта в указанный чат. Сначала создайте чат и объект мониторинга. Удаление привязки не удаляет чат и объект."
    });
    chatSearch.addEventListener("input", renderChats);
    chatForm.addEventListener("submit", event => void saveChat(event));
    bindingForm.addEventListener("submit", event => void saveBinding(event));
    bindingKind.addEventListener("change", renderBindingObjects);
    chatModal.addEventListener("shown.bs.modal", () => chatIdInput.focus());
    bindingModal.addEventListener("shown.bs.modal", () => bindingChat.focus());
    page.addEventListener("click", handleClick);
    await runCommand(loadSettings);
}

async function loadSettings(): Promise<void> {
    loading.classList.remove("d-none");
    content.classList.add("d-none");
    const response = await sendJsonRequest<ServiceResultWithData<NotificationSettings>>(
        "/api/admin/notifications");
    const settings = unwrapServiceResult(response);
    state.chats = settings.chats;
    state.bindings = settings.bindings;
    state.targets = settings.targets;
    if (state.selectedChatId !== null && !state.chats.some(chat => chat.id === state.selectedChatId)) {
        state.selectedChatId = null;
    }
    loading.classList.add("d-none");
    content.classList.remove("d-none");
    render();
}

function render(): void {
    renderChats();
    renderBindingChats();
    renderBindingObjects();
    setSaving(state.saving);
}

function renderChats(): void {
    const rows: HTMLTableRowElement[] = [];
    for (const chat of state.chats) {
        const bindings = state.bindings.filter(binding => binding.telegramChatId === chat.id);
        const chatMatches = matchesSearch(chatSearch.value, chat.name, String(chat.telegramChatId));
        if (bindings.length === 0 && chatMatches) rows.push(createChatRow(chat, null));
        for (const binding of bindings) {
            const target = bindingTarget(binding);
            if (chatMatches || matchesSearch(chatSearch.value, target.displayName, kindText(target.kind))) {
                rows.push(createChatRow(chat, binding));
            }
        }
    }
    chatsTable.body.replaceChildren(...rows);
    chatEmpty.classList.toggle("d-none", rows.length !== 0);
    chatEmpty.textContent = state.chats.length === 0 ? "Чатов пока нет." : "По выбранному фильтру чаты и объекты не найдены.";
    chatsTable.refresh();
}

function createChatRow(chat: TelegramChat, binding: NotificationBinding | null): HTMLTableRowElement {
    const actions = document.createElement("div");
    actions.classList.add("d-flex", "flex-wrap", "gap-1");
    actions.setAttribute("role", "group");
    actions.setAttribute("aria-label", `Действия с чатом ${chat.name}`);
    actions.append(
        createChatActionButton("Отправить тест", "test-chat", chat.id, "btn-outline-secondary"),
        createChatActionButton("Изменить", "edit-chat", chat.id, "btn-outline-secondary"),
        createChatActionButton("Удалить", "delete-chat", chat.id, "btn-outline-danger"));
    if (binding === null) {
        return chatsTable.row([chat.name, String(chat.telegramChatId), "", "", "", actions]);
    }
    const target = bindingTarget(binding);
    const removeButton = document.createElement("button");
    removeButton.type = "button";
    removeButton.classList.add("btn", "btn-sm", "btn-outline-danger");
    removeButton.dataset.action = "delete-binding";
    removeButton.dataset.bindingId = binding.id;
    removeButton.textContent = "Убрать привязку";
    return chatsTable.row([chat.name, String(chat.telegramChatId), kindText(target.kind), target.displayName, removeButton, actions]);
}

function createChatActionButton(
    label: string,
    action: "test-chat" | "edit-chat" | "delete-chat",
    chatId: string,
    colorClass: string
): HTMLButtonElement {
    const button = document.createElement("button");
    button.type = "button";
    button.classList.add("btn", "btn-sm", colorClass);
    button.dataset.action = action;
    button.dataset.chatId = chatId;
    button.textContent = label;
    return button;
}

function bindingTarget(binding: NotificationBinding): NotificationTarget {
    const targetId = binding.serverId ?? binding.containerId;
    if (targetId === null) {
        throw new Error("Привязка не содержит объект.");
    }
    return requireTarget(targetId);
}

function renderBindingChats(): void {
    const currentValue = bindingChat.value;
    bindingChat.replaceChildren();
    for (const chat of state.chats) {
        const option = document.createElement("option");
        option.value = chat.id;
        option.textContent = `${chat.name} (${chat.telegramChatId})`;
        bindingChat.append(option);
    }
    if (state.chats.some(chat => chat.id === currentValue)) {
        bindingChat.value = currentValue;
    }
}

function renderBindingObjects(): void {
    const kind = readKind(bindingKind.value);
    const currentValue = bindingObject.value;
    bindingObject.replaceChildren();
    for (const target of state.targets.filter(item => item.kind === kind)) {
        const option = document.createElement("option");
        option.value = target.id;
        option.textContent = target.displayName;
        bindingObject.append(option);
    }
    if (state.targets.some(target => target.kind === kind && target.id === currentValue)) {
        bindingObject.value = currentValue;
    }
}

function prepareChatForm(): void {
    const selected = state.selectedChatId === null ? null : requireChat(state.selectedChatId);
    chatFormTitle.textContent = selected === null ? "Новый чат" : "Изменить чат";
    chatIdInput.value = selected === null ? "" : String(selected.telegramChatId);
    chatNameInput.value = selected?.name ?? "";
    clearFormErrors();
    hideModalError(chatGeneralError);
}

function openNewChatModal(): void {
    state.selectedChatId = null;
    chatForm.reset();
    prepareChatForm();
    showModal(chatModal);
}

function openEditChatModal(chatId: string): void {
    state.selectedChatId = chatId;
    prepareChatForm();
    showModal(chatModal);
}

function openBindingModal(): void {
    if (state.chats.length === 0) {
        throw new Error("Сначала добавьте Telegram-чат.");
    }
    if (state.targets.length === 0) {
        throw new Error("Нет объектов, доступных для привязки.");
    }

    bindingForm.reset();
    hideModalError(bindingGeneralError);
    bindingKind.value = state.targets.some(target => target.kind === 1) ? "1" : "2";
    renderBindingChats();
    renderBindingObjects();
    showModal(bindingModal);
}

async function saveChat(event: SubmitEvent): Promise<void> {
    event.preventDefault();
    await runSavingCommand(async () => {
        clearFormErrors();
        const payload = {
            telegramChatId: Number(chatIdInput.value),
            name: chatNameInput.value
        };
        const url = state.selectedChatId === null
            ? "/api/admin/notifications/chats"
            : `/api/admin/notifications/chats/${state.selectedChatId}`;
        const method = state.selectedChatId === null ? "POST" : "PUT";
        try {
            const response = await sendJsonRequest<ServiceResultWithData<TelegramChat>>(url, method, payload);
            unwrapServiceResult(response);
            hideModal(chatModal);
            state.selectedChatId = null;
            await loadSettings();
        }
        catch (error: unknown) {
            if (showChatFieldError(error)) {
                return;
            }
            showModalError(chatGeneralError, error, "Не удалось сохранить чат.");
        }
    });
}

async function saveBinding(event: SubmitEvent): Promise<void> {
    event.preventDefault();
    await runSavingCommand(async () => {
        hideModalError(bindingGeneralError);
        try {
            if (!bindingChat.value || !bindingObject.value) {
                throw new Error("Для привязки нужны чат и объект.");
            }
            const response = await sendJsonRequest<ServiceResultWithData<NotificationBinding>>(
                "/api/admin/notifications/bindings",
                "POST",
                {
                    chatId: bindingChat.value,
                    objectKind: readKind(bindingKind.value),
                    objectId: bindingObject.value
                });
            unwrapServiceResult(response);
            hideModal(bindingModal);
            await loadSettings();
        }
        catch (error: unknown) {
            showModalError(bindingGeneralError, error, "Не удалось добавить привязку.");
        }
    });
}

async function testChat(chatId: string): Promise<void> {
    const response = await sendJsonRequest<ServiceResult>(
        `/api/admin/notifications/chats/${chatId}/test`,
        "POST");
    unwrapServiceSuccess(response);
}

async function deleteChat(chatId: string): Promise<void> {
    if (!window.confirm("Удалить чат и все его привязки?")) {
        return;
    }
    const response = await sendJsonRequest<ServiceResult>(
        `/api/admin/notifications/chats/${chatId}`,
        "DELETE");
    unwrapServiceSuccess(response);
    await loadSettings();
}

async function deleteBinding(bindingId: string): Promise<void> {
    if (!window.confirm("Удалить привязку уведомлений?")) {
        return;
    }
    const response = await sendJsonRequest<ServiceResult>(
        `/api/admin/notifications/bindings/${bindingId}`,
        "DELETE");
    unwrapServiceSuccess(response);
    await loadSettings();
}

function handleClick(event: MouseEvent): void {
    const target = event.target;
    if (!(target instanceof Element)) {
        return;
    }
    const actionElement = target.closest<HTMLElement>("[data-action]");
    const action = actionElement?.dataset.action;
    if (!action) {
        return;
    }
    if (action === "new-chat") {
        openNewChatModal();
    }
    else if (action === "new-binding") {
        void runCommand(async () => openBindingModal());
    }
    else if (action === "edit-chat") {
        openEditChatModal(requireData(actionElement, "chatId"));
    }
    else if (action === "test-chat") {
        void runSavingCommand(() => testChat(requireData(actionElement, "chatId")));
    }
    else if (action === "delete-chat") {
        void runSavingCommand(() => deleteChat(requireData(actionElement, "chatId")));
    }
    else if (action === "delete-binding") {
        void runSavingCommand(() => deleteBinding(requireData(actionElement, "bindingId")));
    }
    else {
        throw new Error(`Неизвестное действие: ${action}.`);
    }
}

async function runCommand(command: () => Promise<void>): Promise<void> {
    clearPageError();
    try {
        await command();
    }
    catch (error: unknown) {
        showPageError(error);
    }
}

async function runSavingCommand(command: () => Promise<void>): Promise<void> {
    clearPageError();
    state.saving = true;
    setSaving(true);
    try {
        await command();
    }
    catch (error: unknown) {
        showPageError(error);
    }
    finally {
        state.saving = false;
        setSaving(false);
    }
}

function setSaving(saving: boolean): void {
    for (const element of document.querySelectorAll<HTMLButtonElement | HTMLInputElement | HTMLSelectElement>(
        "[data-notifications-page] button, [data-notifications-page] input, [data-notifications-page] select")) {
        element.disabled = saving;
    }
    newBindingButton.disabled = saving || state.chats.length === 0 || state.targets.length === 0;
    newBindingButton.title = state.chats.length === 0
        ? "Сначала добавьте чат"
        : state.targets.length === 0
            ? "Нет объектов для привязки"
            : "";
}

function showChatFieldError(error: unknown): boolean {
    if (!(error instanceof ApiError) || !error.field) {
        return false;
    }
    const input = chatForm.querySelector<HTMLInputElement>(`[name="${error.field}"]`);
    const message = chatForm.querySelector<HTMLElement>(`[data-chat-error="${error.field}"]`);
    if (!input || !message) {
        return false;
    }
    input.classList.add("is-invalid");
    message.textContent = error.message;
    return true;
}

function clearFormErrors(): void {
    for (const input of chatForm.querySelectorAll<HTMLElement>(".is-invalid")) {
        input.classList.remove("is-invalid");
    }
    for (const message of chatForm.querySelectorAll<HTMLElement>(".invalid-feedback")) {
        message.textContent = "";
    }
}

function showModalError(element: HTMLElement, error: unknown, fallbackMessage: string): void {
    element.textContent = error instanceof Error ? error.message : fallbackMessage;
    element.classList.remove("d-none");
}

function hideModalError(element: HTMLElement): void {
    element.textContent = "";
    element.classList.add("d-none");
}

function showModal(element: Element): void {
    requireBootstrap().Modal.getOrCreateInstance(element).show();
}

function hideModal(element: Element): void {
    requireBootstrap().Modal.getOrCreateInstance(element).hide();
}

function requireBootstrap(): NonNullable<BootstrapWindow["bootstrap"]> {
    const bootstrapApi = (window as BootstrapWindow).bootstrap;
    if (!bootstrapApi) {
        throw new Error("Bootstrap не загружен.");
    }
    return bootstrapApi;
}

function showPageError(error: unknown): void {
    pageError.textContent = error instanceof Error ? error.message : "Неизвестная ошибка.";
    pageError.classList.remove("d-none");
}

function clearPageError(): void {
    pageError.textContent = "";
    pageError.classList.add("d-none");
}

function requireChat(id: string): TelegramChat {
    const chat = state.chats.find(item => item.id === id);
    if (!chat) {
        throw new Error("Чат не найден в состоянии страницы.");
    }
    return chat;
}

function requireTarget(id: string): NotificationTarget {
    const target = state.targets.find(item => item.id === id);
    if (!target) {
        throw new Error("Объект не найден в состоянии страницы.");
    }
    return target;
}

function requireData(element: HTMLElement, name: string): string {
    const value = element.dataset[name];
    if (!value) {
        throw new Error(`Отсутствует data-${name}.`);
    }
    return value;
}

function readKind(value: string): MonitoringObjectKind {
    const numericValue = Number(value);
    if (numericValue === 1 || numericValue === 2) {
        return numericValue;
    }
    throw new Error(`Неизвестный тип объекта: ${value}.`);
}

function kindText(kind: MonitoringObjectKind): string {
    if (kind === 1) {
        return "Сервер";
    }
    if (kind === 2) {
        return "Контейнер";
    }
    throw new Error(`Неизвестный тип объекта: ${String(kind)}.`);
}

function requireElement<TElement extends Element>(selector: string): TElement {
    const element = document.querySelector<TElement>(selector);
    if (!element) {
        throw new Error(`Обязательный элемент не найден: ${selector}.`);
    }
    return element;
}
