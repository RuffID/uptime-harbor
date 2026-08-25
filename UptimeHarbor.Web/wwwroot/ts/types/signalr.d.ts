declare namespace signalR {
    enum LogLevel {
        Warning = 3
    }

    interface HubConnection {
        start(): Promise<void>;
        on(methodName: string, handler: (...args: unknown[]) => void): void;
        onreconnecting(handler: (error?: Error) => void): void;
        onreconnected(handler: (connectionId?: string) => void): void;
        onclose(handler: (error?: Error) => void): void;
    }

    class HubConnectionBuilder {
        withUrl(url: string): HubConnectionBuilder;
        withAutomaticReconnect(retryDelays: number[]): HubConnectionBuilder;
        configureLogging(logLevel: LogLevel): HubConnectionBuilder;
        build(): HubConnection;
    }
}
