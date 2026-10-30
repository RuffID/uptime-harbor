/** Ввод и идентификаторы живут только в памяти вкладки, включая ещё не отправленный пароль. */
export class CommandDraft<T extends object> {
    private sent: T | null = null;
    private fingerprint: string | null = null;

    prepare(value: T): T {
        const fingerprint = JSON.stringify(value);
        if (this.sent !== null && this.fingerprint !== fingerprint) {
            throw new Error("Запрос с этим OperationId уже отправлялся. Повторите исходный запрос либо создайте новую команду от актуальной ревизии; прежняя могла быть принята.");
        }
        if (this.sent === null) {
            this.sent = structuredClone(value);
            this.fingerprint = fingerprint;
        }
        return structuredClone(this.sent);
    }

    clear(): void { this.sent = null; this.fingerprint = null; }
}

/** Отменённый или поздний ответ не получает права перерисовать новое представление. */
export class LatestRequest {
    private version = 0;
    private controller: AbortController | null = null;

    begin(): { version: number; signal: AbortSignal } {
        this.invalidate();
        this.controller = new AbortController();
        return { version: this.version, signal: this.controller.signal };
    }

    isCurrent(version: number): boolean { return version === this.version; }
    invalidate(): void { this.version++; this.controller?.abort(); this.controller = null; }
}
