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
const bindingList = requireElement<HTMLElement>("[data-binding-list]");
const bindingEmpty = requireElement<HTMLElement>("[data-binding-empty]");
const chatForm = requireElement<HTMLFormElement>("[data-chat-form]");
const chatFormTitle = requireElement<HTMLElement>("[data-chat-form-title]");
const chatIdInput = requireElement<HTMLInputElement>("[data-chat-id]");
const chatNameInput = requireElement<HTMLInputElement>("[data-chat-name]");
const bindingForm = requireElement<HTMLFormElement>("[data-binding-form]");
const bindingChat = requireElement<HTMLSelectElement>("[data-binding-chat]");
const bindingKind = requireElement<HTMLSelectElement>("[data-binding-kind]");
const bindingObject = requireElement<HTMLSelectElement>("[data-binding-object]");

document.addEventListener("DOMContentLoaded", () => {
    void initNotificationsPage();
});

async function initNotificationsPage(): Promise<void> {
    chatForm.addEventListener("submit", event => void saveChat(event));
    bindingForm.addEventListener("submit", event => void saveBinding(event));
    bindingKind.addEventListener("change", renderBindingObjects);
    requireElement<HTMLElement>("[data-notifications-page]").addEventListener("click", handleClick);
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
    renderBindings();
    renderBindingChats();
    renderBindingObjects();
    renderChatForm();
    setSaving(state.saving);
}

function renderChats(): void {
    chatList.replaceChildren();
    chatEmpty.classList.toggle("d-none", state.chats.length !== 0);
    for (const chat of state.chats) {
        const button = document.createElement("button");
        button.type = "button";
        button.classList.add("list-group-item", "list-group-item-action");
        button.classList.toggle("active", chat.id === state.selectedChatId);
        button.dataset.action = "select-chat";
        button.dataset.chatId = chat.id;

        const name = document.createElement("span");
        name.classList.add("d-block", "fw-semibold");
        name.textContent = chat.name;
        const identifier = document.createElement("span");
        identifier.classList.add("d-block", "small");
        identifier.textContent = String(chat.telegramChatId);
        button.append(name, identifier);
        chatList.append(button);
    }
}

function renderBindings(): void {
    bindingList.replaceChildren();
    bindingEmpty.classList.toggle("d-none", state.bindings.length !== 0);
    for (const binding of state.bindings) {
        const row = document.createElement("div");
        row.classList.add("list-group-item", "d-flex", "justify-content-between", "align-items-center", "gap-2");
        const chat = requireChat(binding.telegramChatId);
        const targetId = binding.serverId ?? binding.containerId;
        if (targetId === null) {
            throw new Error("Привязка не содержит объект.");
        }
        const target = requireTarget(targetId);

        const text = document.createElement("span");
        text.textContent = `${chat.name} → ${kindText(target.kind)}: ${target.displayName}`;
        const removeButton = document.createElement("button");
        removeButton.type = "button";
        removeButton.classList.add("btn", "btn-sm", "btn-outline-danger");
        removeButton.dataset.action = "delete-binding";
        removeButton.dataset.bindingId = binding.id;
        removeButton.textContent = "Удалить";
        row.append(text, removeButton);
        bindingList.append(row);
    }
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

function renderChatForm(): void {
    const selected = state.selectedChatId === null ? null : requireChat(state.selectedChatId);
    chatFormTitle.textContent = selected === null ? "Новый чат" : "Изменить чат";
    chatIdInput.value = selected === null ? "" : String(selected.telegramChatId);
    chatNameInput.value = selected?.name ?? "";
    toggleAction("cancel-chat", selected !== null);
    toggleAction("test-chat", selected !== null);
    toggleAction("delete-chat", selected !== null);
    clearFormErrors();
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
            const saved = unwrapServiceResult(response);
            state.selectedChatId = saved.id;
            await loadSettings();
        }
        catch (error: unknown) {
            if (showChatFieldError(error)) {
                return;
            }
            throw error;
        }
    });
}

async function saveBinding(event: SubmitEvent): Promise<void> {
    event.preventDefault();
    await runSavingCommand(async () => {
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
        await loadSettings();
    });
}

async function testChat(): Promise<void> {
    const response = await sendJsonRequest<ServiceResult>(
        `/api/admin/notifications/chats/${requireSelectedChatId()}/test`,
        "POST");
    unwrapServiceSuccess(response);
}

async function deleteChat(): Promise<void> {
    if (!window.confirm("Удалить чат и все его привязки?")) {
        return;
    }
    const response = await sendJsonRequest<ServiceResult>(
        `/api/admin/notifications/chats/${requireSelectedChatId()}`,
        "DELETE");
    unwrapServiceSuccess(response);
    state.selectedChatId = null;
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
    if (action === "select-chat") {
        state.selectedChatId = requireData(actionElement, "chatId");
        render();
    }
    else if (action === "cancel-chat") {
        state.selectedChatId = null;
        render();
    }
    else if (action === "test-chat") {
        void runSavingCommand(testChat);
    }
    else if (action === "delete-chat") {
        void runSavingCommand(deleteChat);
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

function showPageError(error: unknown): void {
    pageError.textContent = error instanceof Error ? error.message : "Неизвестная ошибка.";
    pageError.classList.remove("d-none");
}

function clearPageError(): void {
    pageError.textContent = "";
    pageError.classList.add("d-none");
}

function toggleAction(action: string, visible: boolean): void {
    requireElement<HTMLElement>(`[data-action="${action}"]`).classList.toggle("d-none", !visible);
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

function requireSelectedChatId(): string {
    if (state.selectedChatId === null) {
        throw new Error("Чат не выбран.");
    }
    return state.selectedChatId;
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
