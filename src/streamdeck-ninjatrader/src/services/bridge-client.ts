import WebSocket from 'ws';
import { BridgeMessage, TradingState } from '../models/messages.js';
import * as log from '../utils/logger.js';

type MessageHandler = (msg: BridgeMessage) => void;
type StateHandler = (state: TradingState) => void;
type ConnectionHandler = (connected: boolean) => void;

/**
 * WebSocket client that connects to the bridge.
 * Handles reconnection, message routing, and state tracking.
 */
export class BridgeClient {
  private ws: WebSocket | null = null;
  private url: string;
  private reconnectDelay: number;
  private reconnectTimer: ReturnType<typeof setTimeout> | null = null;
  private running = false;

  private messageHandlers: MessageHandler[] = [];
  private stateHandlers: StateHandler[] = [];
  private connectionHandlers: ConnectionHandler[] = [];

  // Pending request callbacks keyed by requestId
  private pendingRequests = new Map<string, {
    resolve: (msg: BridgeMessage) => void;
    timer: ReturnType<typeof setTimeout>;
  }>();

  private _lastState: TradingState | null = null;

  get isConnected(): boolean {
    return this.ws?.readyState === WebSocket.OPEN;
  }

  get lastState(): TradingState | null {
    return this._lastState;
  }

  constructor(url: string, reconnectDelay = 3000) {
    this.url = url;
    this.reconnectDelay = reconnectDelay;
  }

  start(): void {
    if (this.running) return;
    this.running = true;
    this.connect();
    log.info(`Bridge client started, connecting to ${this.url}`);
  }

  stop(): void {
    this.running = false;
    if (this.reconnectTimer) {
      clearTimeout(this.reconnectTimer);
      this.reconnectTimer = null;
    }
    this.closeSocket();
    log.info('Bridge client stopped');
  }

  onMessage(handler: MessageHandler): void {
    this.messageHandlers.push(handler);
  }

  onStateUpdate(handler: StateHandler): void {
    this.stateHandlers.push(handler);
  }

  onConnectionChange(handler: ConnectionHandler): void {
    this.connectionHandlers.push(handler);
  }

  /**
   * Send a command and wait for the response (with timeout).
   */
  async sendCommand(msg: BridgeMessage, timeoutMs = 10000): Promise<BridgeMessage> {
    if (!this.isConnected) {
      return {
        type: 'error',
        version: '1.0',
        requestId: msg.requestId,
        timestamp: new Date().toISOString(),
        source: 'plugin',
        action: msg.action,
        result: { success: false },
        error: { code: 'NOT_CONNECTED', message: 'Not connected to bridge' },
      };
    }

    return new Promise<BridgeMessage>((resolve) => {
      const timer = setTimeout(() => {
        this.pendingRequests.delete(msg.requestId!);
        resolve({
          type: 'error',
          version: '1.0',
          requestId: msg.requestId,
          timestamp: new Date().toISOString(),
          source: 'plugin',
          action: msg.action,
          result: { success: false },
          error: { code: 'TIMEOUT', message: 'Request timed out' },
        });
      }, timeoutMs);

      this.pendingRequests.set(msg.requestId!, { resolve, timer });

      const json = JSON.stringify(msg);
      this.ws!.send(json);
      log.debug(`Sent: ${msg.action} [REQ:${msg.requestId}]`);
    });
  }

  /**
   * Fire-and-forget send (for commands where we don't need the response).
   */
  send(msg: BridgeMessage): void {
    if (!this.isConnected) {
      log.warn('Cannot send — not connected to bridge');
      return;
    }
    const json = JSON.stringify(msg);
    this.ws!.send(json);
    log.debug(`Sent: ${msg.action}`);
  }

  private connect(): void {
    try {
      this.ws = new WebSocket(this.url);

      this.ws.on('open', () => {
        log.info('Connected to bridge');
        this.notifyConnection(true);
      });

      this.ws.on('message', (data: WebSocket.Data) => {
        try {
          const msg: BridgeMessage = JSON.parse(data.toString());
          this.handleMessage(msg);
        } catch (err) {
          log.warn(`Invalid JSON from bridge: ${err}`);
        }
      });

      this.ws.on('close', () => {
        log.info('Disconnected from bridge');
        this.notifyConnection(false);
        this.scheduleReconnect();
      });

      this.ws.on('error', (err: Error) => {
        log.warn(`WebSocket error: ${err.message}`);
      });
    } catch (err) {
      log.error(`Connection failed: ${err}`);
      this.scheduleReconnect();
    }
  }

  private handleMessage(msg: BridgeMessage): void {
    // Check if this is a response to a pending request
    if (msg.type === 'response' && msg.requestId && this.pendingRequests.has(msg.requestId)) {
      const pending = this.pendingRequests.get(msg.requestId)!;
      clearTimeout(pending.timer);
      this.pendingRequests.delete(msg.requestId);
      pending.resolve(msg);
      return;
    }

    // Handle state updates
    if (msg.type === 'event' && msg.action === 'stateUpdate' && msg.payload) {
      this._lastState = msg.payload as unknown as TradingState;
      for (const handler of this.stateHandlers) {
        try { handler(this._lastState); } catch (e) { /* skip */ }
      }
      return;
    }

    // Generic message handlers
    for (const handler of this.messageHandlers) {
      try { handler(msg); } catch (e) { /* skip */ }
    }
  }

  private scheduleReconnect(): void {
    if (!this.running) return;
    log.info(`Reconnecting in ${this.reconnectDelay}ms...`);
    this.reconnectTimer = setTimeout(() => {
      this.reconnectTimer = null;
      if (this.running) this.connect();
    }, this.reconnectDelay);
  }

  private closeSocket(): void {
    try {
      if (this.ws && this.ws.readyState === WebSocket.OPEN) {
        this.ws.close();
      }
    } catch { /* ignore */ }
    this.ws = null;
  }

  private notifyConnection(connected: boolean): void {
    for (const handler of this.connectionHandlers) {
      try { handler(connected); } catch (e) { /* skip */ }
    }
  }
}
