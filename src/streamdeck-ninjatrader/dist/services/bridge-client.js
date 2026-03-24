import WebSocket from 'ws';
import * as log from '../utils/logger.js';
/**
 * WebSocket client that connects to the bridge.
 * Handles reconnection, message routing, and state tracking.
 */
export class BridgeClient {
    ws = null;
    url;
    reconnectDelay;
    reconnectTimer = null;
    running = false;
    messageHandlers = [];
    stateHandlers = [];
    connectionHandlers = [];
    // Pending request callbacks keyed by requestId
    pendingRequests = new Map();
    _lastState = null;
    get isConnected() {
        return this.ws?.readyState === WebSocket.OPEN;
    }
    get lastState() {
        return this._lastState;
    }
    constructor(url, reconnectDelay = 3000) {
        this.url = url;
        this.reconnectDelay = reconnectDelay;
    }
    start() {
        if (this.running)
            return;
        this.running = true;
        this.connect();
        log.info(`Bridge client started, connecting to ${this.url}`);
    }
    stop() {
        this.running = false;
        if (this.reconnectTimer) {
            clearTimeout(this.reconnectTimer);
            this.reconnectTimer = null;
        }
        this.closeSocket();
        log.info('Bridge client stopped');
    }
    onMessage(handler) {
        this.messageHandlers.push(handler);
    }
    onStateUpdate(handler) {
        this.stateHandlers.push(handler);
    }
    onConnectionChange(handler) {
        this.connectionHandlers.push(handler);
    }
    /**
     * Send a command and wait for the response (with timeout).
     */
    async sendCommand(msg, timeoutMs = 10000) {
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
        return new Promise((resolve) => {
            const timer = setTimeout(() => {
                this.pendingRequests.delete(msg.requestId);
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
            this.pendingRequests.set(msg.requestId, { resolve, timer });
            const json = JSON.stringify(msg);
            this.ws.send(json);
            log.debug(`Sent: ${msg.action} [REQ:${msg.requestId}]`);
        });
    }
    /**
     * Fire-and-forget send (for commands where we don't need the response).
     */
    send(msg) {
        if (!this.isConnected) {
            log.warn('Cannot send — not connected to bridge');
            return;
        }
        const json = JSON.stringify(msg);
        this.ws.send(json);
        log.debug(`Sent: ${msg.action}`);
    }
    connect() {
        try {
            this.ws = new WebSocket(this.url);
            this.ws.on('open', () => {
                log.info('Connected to bridge');
                this.notifyConnection(true);
            });
            this.ws.on('message', (data) => {
                try {
                    const msg = JSON.parse(data.toString());
                    this.handleMessage(msg);
                }
                catch (err) {
                    log.warn(`Invalid JSON from bridge: ${err}`);
                }
            });
            this.ws.on('close', () => {
                log.info('Disconnected from bridge');
                this.notifyConnection(false);
                this.scheduleReconnect();
            });
            this.ws.on('error', (err) => {
                log.warn(`WebSocket error: ${err.message}`);
            });
        }
        catch (err) {
            log.error(`Connection failed: ${err}`);
            this.scheduleReconnect();
        }
    }
    handleMessage(msg) {
        // Check if this is a response to a pending request
        if (msg.type === 'response' && msg.requestId && this.pendingRequests.has(msg.requestId)) {
            const pending = this.pendingRequests.get(msg.requestId);
            clearTimeout(pending.timer);
            this.pendingRequests.delete(msg.requestId);
            pending.resolve(msg);
            return;
        }
        // Handle state updates
        if (msg.type === 'event' && msg.action === 'stateUpdate' && msg.payload) {
            this._lastState = msg.payload;
            for (const handler of this.stateHandlers) {
                try {
                    handler(this._lastState);
                }
                catch (e) { /* skip */ }
            }
            return;
        }
        // Generic message handlers
        for (const handler of this.messageHandlers) {
            try {
                handler(msg);
            }
            catch (e) { /* skip */ }
        }
    }
    scheduleReconnect() {
        if (!this.running)
            return;
        log.info(`Reconnecting in ${this.reconnectDelay}ms...`);
        this.reconnectTimer = setTimeout(() => {
            this.reconnectTimer = null;
            if (this.running)
                this.connect();
        }, this.reconnectDelay);
    }
    closeSocket() {
        try {
            if (this.ws && this.ws.readyState === WebSocket.OPEN) {
                this.ws.close();
            }
        }
        catch { /* ignore */ }
        this.ws = null;
    }
    notifyConnection(connected) {
        for (const handler of this.connectionHandlers) {
            try {
                handler(connected);
            }
            catch (e) { /* skip */ }
        }
    }
}
//# sourceMappingURL=bridge-client.js.map