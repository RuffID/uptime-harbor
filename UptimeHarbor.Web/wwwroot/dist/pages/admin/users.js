import { ApiError, sendJsonRequest, unwrapServiceResult, unwrapServiceSuccess } from "../../shared/http.js";
const state = {
    users: [], servers: [], selectedUserId: null, creating: false, saving: false
};
const page = requireElement("[data-users-page]");
const pageError = requireElement("[data-page-error]");
const pageSuccess = requireElement("[data-page-success]");
const userList = requireElement("[data-user-list]");
const userEmpty = requireElement("[data-user-empty]");
const placeholder = requireElement("[data-selection-placeholder]");
const createForm = requireElement("[data-create-form]");
const createLogin = requireElement("[data-create-login]");
const createPassword = requireElement("[data-create-password]");
const details = requireElement("[data-user-details]");
const userTitle = requireElement("[data-user-title]");
const userRole = requireElement("[data-user-role]");
const userEnabled = requireElement("[data-user-enabled]");
const visibilityList = requireElement("[data-visibility-list]");
const currentUserId = document.querySelector('meta[name="current-user-id"]')?.content ?? "";
document.addEventListener("DOMContentLoaded", () => { void initUsersPage(); });
async function initUsersPage() {
    page.addEventListener("click", handleClick);
    createForm.addEventListener("submit", event => void createUser(event));
    await runCommand(loadData);
}
async function loadData() {
    const usersResponse = await sendJsonRequest("/api/admin/users");
    state.users = unwrapServiceResult(usersResponse);
    const serversResponse = await sendJsonRequest("/api/admin/objects/servers");
    const servers = unwrapServiceResult(serversResponse);
    state.servers = await Promise.all(servers.map(async (server) => {
        const response = await sendJsonRequest(`/api/admin/objects/servers/${server.id}/containers`);
        return { ...server, containers: unwrapServiceResult(response) };
    }));
    if (state.selectedUserId !== null && !state.users.some(user => user.id === state.selectedUserId))
        state.selectedUserId = null;
    render();
}
function render() {
    userList.replaceChildren();
    userEmpty.classList.toggle("d-none", state.users.length !== 0);
    for (const user of state.users) {
        const button = document.createElement("button");
        button.type = "button";
        button.classList.add("list-group-item", "list-group-item-action");
        button.classList.toggle("active", user.id === state.selectedUserId);
        button.dataset.action = "select-user";
        button.dataset.userId = user.id;
        button.textContent = `${user.login}${user.isAdministrator ? " (администратор)" : ""}`;
        userList.append(button);
    }
    const selected = state.selectedUserId === null ? null : requireUser(state.selectedUserId);
    placeholder.classList.toggle("d-none", selected !== null || state.creating);
    createForm.classList.toggle("d-none", !state.creating);
    details.classList.toggle("d-none", selected === null || state.creating);
    if (selected) {
        userTitle.textContent = selected.login;
        userRole.textContent = selected.isAdministrator ? "Администратор" : "Обычный пользователь";
        userEnabled.checked = selected.isEnabled;
        userEnabled.disabled = selected.isAdministrator || selected.id === currentUserId;
        requireElement('[data-action="delete-user"]').classList.toggle("d-none", selected.isAdministrator || selected.id === currentUserId);
        renderVisibility(selected);
    }
    setSaving(state.saving);
}
function renderVisibility(user) {
    visibilityList.replaceChildren();
    for (const server of state.servers) {
        const wrapper = document.createElement("div");
        wrapper.classList.add("border", "rounded", "p-2");
        const serverInput = document.createElement("input");
        serverInput.type = "checkbox";
        serverInput.id = `server_${server.id}`;
        serverInput.name = "serverIds";
        serverInput.value = server.id;
        serverInput.checked = user.serverIds.includes(server.id);
        serverInput.dataset.serverId = server.id;
        serverInput.classList.add("form-check-input");
        const serverLabel = document.createElement("label");
        serverLabel.htmlFor = serverInput.id;
        serverLabel.classList.add("form-check-label", "fw-semibold");
        serverLabel.textContent = server.lastKnownName ?? server.endpointUrl;
        const serverRow = document.createElement("div");
        serverRow.classList.add("form-check");
        serverRow.append(serverInput, serverLabel);
        wrapper.append(serverRow);
        const containerList = document.createElement("div");
        containerList.classList.add("ms-4");
        for (const container of server.containers) {
            const row = document.createElement("div");
            row.classList.add("form-check");
            const input = document.createElement("input");
            input.type = "checkbox";
            input.id = `container_${container.id}`;
            input.name = "containerIds";
            input.value = container.id;
            input.dataset.parentServerId = server.id;
            input.checked = user.containerIds.includes(container.id);
            input.disabled = !serverInput.checked;
            input.classList.add("form-check-input");
            const label = document.createElement("label");
            label.htmlFor = input.id;
            label.classList.add("form-check-label");
            label.textContent = container.lastKnownName ?? container.endpointUrl;
            row.append(input, label);
            containerList.append(row);
        }
        serverInput.addEventListener("change", () => {
            for (const input of containerList.querySelectorAll("input")) {
                input.disabled = !serverInput.checked;
                if (!serverInput.checked)
                    input.checked = false;
            }
        });
        wrapper.append(containerList);
        visibilityList.append(wrapper);
    }
}
async function createUser(event) {
    event.preventDefault();
    await runSavingCommand(async () => {
        clearFieldErrors();
        try {
            const response = await sendJsonRequest("/api/admin/users", "POST", { login: createLogin.value, initialPassword: createPassword.value });
            const created = unwrapServiceResult(response);
            state.creating = false;
            state.selectedUserId = created.id;
            showSuccess("Пользователь создан.");
            await loadData();
        }
        catch (error) {
            if (!showCreateFieldError(error))
                throw error;
        }
    });
}
async function saveVisibility() {
    const user = requireSelectedUser();
    await runSavingCommand(async () => {
        const serverIds = Array.from(visibilityList.querySelectorAll('input[name="serverIds"]:checked')).map(input => input.value);
        const containerIds = Array.from(visibilityList.querySelectorAll('input[name="containerIds"]:checked')).map(input => input.value);
        const enabledResponse = await sendJsonRequest(`/api/admin/users/${user.id}/enabled`, "PUT", { isEnabled: userEnabled.checked });
        unwrapServiceSuccess(enabledResponse);
        const visibilityResponse = await sendJsonRequest(`/api/admin/users/${user.id}/visibility`, "PUT", { serverIds, containerIds });
        unwrapServiceSuccess(visibilityResponse);
        showSuccess("Изменения сохранены.");
        await loadData();
    });
}
async function deleteUser() {
    const user = requireSelectedUser();
    if (user.isAdministrator || user.id === currentUserId)
        throw new Error("Эту учётную запись нельзя удалить.");
    if (!window.confirm(`Удалить пользователя «${user.login}» и его права?`))
        return;
    await runSavingCommand(async () => { const response = await sendJsonRequest(`/api/admin/users/${user.id}`, "DELETE"); unwrapServiceSuccess(response); state.selectedUserId = null; showSuccess("Пользователь удалён."); await loadData(); });
}
function handleClick(event) {
    const target = event.target;
    if (!(target instanceof Element))
        return;
    const element = target.closest("[data-action]");
    const action = element?.dataset.action;
    if (!action)
        return;
    if (action === "new-user") {
        state.creating = true;
        state.selectedUserId = null;
        createForm.reset();
        render();
        createLogin.focus();
    }
    else if (action === "select-user") {
        state.creating = false;
        state.selectedUserId = requireData(element, "userId");
        render();
    }
    else if (action === "save-visibility")
        void runCommand(saveVisibility);
    else if (action === "delete-user")
        void runCommand(deleteUser);
    else
        throw new Error(`Неизвестное действие: ${action}.`);
}
async function runCommand(command) { clearPageError(); try {
    await command();
}
catch (error) {
    showPageError(error);
} }
async function runSavingCommand(command) { state.saving = true; setSaving(true); try {
    await command();
}
finally {
    state.saving = false;
    setSaving(false);
} }
function setSaving(saving) { for (const element of page.querySelectorAll("button, input"))
    element.disabled = saving; }
function showCreateFieldError(error) { if (!(error instanceof ApiError) || !error.field)
    return false; const input = createForm.querySelector(`[name="${error.field}"]`); const message = createForm.querySelector(`[data-create-error="${error.field}"]`); if (!input || !message)
    return false; input.classList.add("is-invalid"); message.textContent = error.message; return true; }
function clearFieldErrors() { for (const input of createForm.querySelectorAll(".is-invalid"))
    input.classList.remove("is-invalid"); for (const message of createForm.querySelectorAll(".invalid-feedback"))
    message.textContent = ""; }
function showPageError(error) { pageError.textContent = error instanceof Error ? error.message : "Неизвестная ошибка."; pageError.classList.remove("d-none"); }
function clearPageError() { pageError.textContent = ""; pageError.classList.add("d-none"); }
function showSuccess(message) { pageSuccess.textContent = message; pageSuccess.classList.remove("d-none"); }
function requireUser(id) { const user = state.users.find(item => item.id === id); if (!user)
    throw new Error("Пользователь не найден."); return user; }
function requireSelectedUser() { if (!state.selectedUserId)
    throw new Error("Пользователь не выбран."); return requireUser(state.selectedUserId); }
function requireData(element, name) { const value = element.dataset[name]; if (!value)
    throw new Error(`Отсутствует data-${name}.`); return value; }
function requireElement(selector) { const element = document.querySelector(selector); if (!element)
    throw new Error(`Обязательный элемент не найден: ${selector}.`); return element; }
