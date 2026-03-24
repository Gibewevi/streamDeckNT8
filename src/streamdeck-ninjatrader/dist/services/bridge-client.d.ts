import { BridgeMessage, TradingState } from '../models/messages.js';
type MessageHandler = (msg: BridgeMessage) => void;
type StateHandler = (state: TradingState) => void;
type ConnectionHandler = (connected: boolean) => void;
/**
 * WebSocket client that connects to the bridge.
 * Handles reconnection, message routing, and state tracking.
 */
export declare class BridgeClient {
    private ws;
    private url;
    private reconnectDelay;
    private reconnectTimer;
    private running;
    private messageHandlers;
    private stateHandlers;
    private connectionHandlers;
    private pendingRequests;
    private _lastState;
    get isConnected(): boolean;
    get lastState(): TradingState | null;
    constructor(url: string, reconnectDelay?: number);
    start(): void;
    stop(): void;
    onMessage(handler: MessageHandler): void;
    onStateUpdate(handler: StateHandler): void;
    onConnectionChange(handler: ConnectionHandler): void;
    /**
     * Send a command and wait for the response (with timeout).
     */
    sendCommand(msg: BridgeMessage, timeoutMs?: number): Promise<BridgeMessage>;
    /**
     * Fire-and-forget send (for commands where we don't need the response).
     */
    send(msg: BridgeMessage): void;
    private connect;
    private handleMessage;
    private scheduleReconnect;
    private closeSocket;
    private notifyConnection;
}
export {};
