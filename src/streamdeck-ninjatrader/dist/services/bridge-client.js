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
        log.event('Connection', 'Bridge client started', { url: this.url, reconnectDelayMs: this.reconnectDelay });
    }
    stop() {
        this.running = false;
        if (this.reconnectTimer) {
            clearTimeout(this.reconnectTimer);
            this.reconnectTimer = null;
        }
        this.closeSocket();
        log.event('Connection', 'Bridge client stopped');
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
            log.eventWarn('Wire', `Command ${msg.action} not sent — bridge is not connected`, {
                req: msg.requestId ?? '',
                url: this.url,
            });
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
                // A timeout means the command may or may not have reached the market: the bridge
                // never answered. This is the single most important line to find afterwards.
                log.eventWarn('Wire', `Command ${msg.action} TIMED OUT after ${timeoutMs}ms — outcome unknown`, {
                    req: msg.requestId ?? '',
                });
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
            log.traceEvent('Wire', `→ bridge: ${msg.action}`, { req: msg.requestId ?? '', frame: json });
        });
    }
    /**
     * Fire-and-forget send (for commands where we don't need the response).
     */
    send(msg) {
        if (!this.isConnected) {
            log.eventWarn('Wire', `Fire-and-forget ${msg.action} dropped — bridge is not connected`);
            return;
        }
        const json = JSON.stringify(msg);
        this.ws.send(json);
        log.traceEvent('Wire', `→ bridge: ${msg.action} (no response expected)`, { frame: json });
    }
    connect() {
        try {
            this.ws = new WebSocket(this.url);
            this.ws.on('open', () => {
                log.event('Connection', 'WebSocket open to bridge', { url: this.url });
                this.notifyConnection(true);
            });
            this.ws.on('message', (data) => {
                const raw = data.toString();
                try {
                    const msg = JSON.parse(raw);
                    this.handleMessage(msg);
                }
                catch (err) {
                    log.fail('Wire', err, 'Invalid JSON from bridge — frame dropped', { frame: raw.slice(0, 500) });
                }
            });
            this.ws.on('close', (code, reason) => {
                log.eventWarn('Connection', 'WebSocket closed by the bridge', {
                    code,
                    reason: reason?.toString() || '(none)',
                });
                this.notifyConnection(false);
                this.scheduleReconnect();
            });
            this.ws.on('error', (err) => {
                // ECONNREFUSED on every retry while the bridge is down is expected noise, so this
                // stays at warn rather than error — the connection state change tells the real story.
                log.eventWarn('Connection', `WebSocket error: ${err.message}`, { url: this.url });
            });
        }
        catch (err) {
            log.fail('Connection', err, 'Could not open a WebSocket to the bridge', { url: this.url });
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
                catch (e) {
                    log.fail('Wire', e, 'A state handler threw — the deck may be showing stale data');
                }
            }
            return;
        }
        // A response nobody is waiting for: the request already timed out, or the bridge answered
        // twice. Either way the key gave up before this arrived, which is worth knowing.
        if (msg.type === 'response' && msg.requestId) {
            log.eventWarn('Wire', `Late or unmatched response for ${msg.action}`, {
                req: msg.requestId,
                error: msg.error ? `${msg.error.code}: ${msg.error.message}` : undefined,
            });
        }
        // Generic message handlers
        for (const handler of this.messageHandlers) {
            try {
                handler(msg);
            }
            catch (e) {
                log.fail('Wire', e, `A message handler threw while processing ${msg.type}/${msg.action}`);
            }
        }
    }
    scheduleReconnect() {
        if (!this.running)
            return;
        log.debugEvent('Connection', `Reconnecting in ${this.reconnectDelay}ms`);
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
            catch (e) {
                log.fail('Connection', e, `A connection handler threw (connected=${connected})`);
            }
        }
    }
}
//# sourceMappingURL=bridge-client.js.map