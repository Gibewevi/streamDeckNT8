import WebSocket from 'ws';
import { BridgeMessage, TradingState } from './messages.js';
import * as log from './logger.js';

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

  /**
   * Requêtes déjà acquittées, avec leur horodatage. Sert uniquement à reconnaître la seconde
   * réponse — la confirmation NinjaTrader — d'une action locale transmise à NT8.
   */
  private answered = new Map<string, number>();

  private static readonly ANSWERED_TTL_MS = 60_000;

  private pruneAnswered(): void {
    const cutoff = Date.now() - BridgeClient.ANSWERED_TTL_MS;
    for (const [id, at] of this.answered) {
      if (at < cutoff) this.answered.delete(id);
    }
  }

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
    log.event('Connection', 'Bridge client started', { url: this.url, reconnectDelayMs: this.reconnectDelay });
  }

  stop(): void {
    this.running = false;
    if (this.reconnectTimer) {
      clearTimeout(this.reconnectTimer);
      this.reconnectTimer = null;
    }
    this.closeSocket();
    log.event('Connection', 'Bridge client stopped');
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

    return new Promise<BridgeMessage>((resolve) => {
      const timer = setTimeout(() => {
        this.pendingRequests.delete(msg.requestId!);
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

      this.pendingRequests.set(msg.requestId!, { resolve, timer });

      const json = JSON.stringify(msg);
      this.ws!.send(json);
      log.traceEvent('Wire', `→ bridge: ${msg.action}`, { req: msg.requestId ?? '', frame: json });
    });
  }

  /**
   * Fire-and-forget send (for commands where we don't need the response).
   */
  send(msg: BridgeMessage): void {
    if (!this.isConnected) {
      log.eventWarn('Wire', `Fire-and-forget ${msg.action} dropped — bridge is not connected`);
      return;
    }
    const json = JSON.stringify(msg);
    this.ws!.send(json);
    log.traceEvent('Wire', `→ bridge: ${msg.action} (no response expected)`, { frame: json });
  }

  private connect(): void {
    try {
      this.ws = new WebSocket(this.url);

      this.ws.on('open', () => {
        log.event('Connection', 'WebSocket open to bridge', { url: this.url });
        this.notifyConnection(true);
      });

      this.ws.on('message', (data: WebSocket.Data) => {
        const raw = data.toString();
        try {
          const msg: BridgeMessage = JSON.parse(raw);
          this.handleMessage(msg);
        } catch (err) {
          log.fail('Wire', err, 'Invalid JSON from bridge — frame dropped', { frame: raw.slice(0, 500) });
        }
      });

      this.ws.on('close', (code: number, reason: Buffer) => {
        log.eventWarn('Connection', 'WebSocket closed by the bridge', {
          code,
          reason: reason?.toString() || '(none)',
        });
        this.notifyConnection(false);
        this.scheduleReconnect();
      });

      this.ws.on('error', (err: Error) => {
        // ECONNREFUSED on every retry while the bridge is down is expected noise, so this
        // stays at warn rather than error — the connection state change tells the real story.
        log.eventWarn('Connection', `WebSocket error: ${err.message}`, { url: this.url });
      });
    } catch (err) {
      log.fail('Connection', err, 'Could not open a WebSocket to the bridge', { url: this.url });
      this.scheduleReconnect();
    }
  }

  private handleMessage(msg: BridgeMessage): void {
    // Check if this is a response to a pending request
    if (msg.type === 'response' && msg.requestId && this.pendingRequests.has(msg.requestId)) {
      const pending = this.pendingRequests.get(msg.requestId)!;
      clearTimeout(pending.timer);
      this.pendingRequests.delete(msg.requestId);
      // Une action locale transmise à NT8 (setInstrument, setAccount…) reçoit DEUX réponses de
      // même requestId : le résultat local du bridge, puis la confirmation de NinjaTrader. On
      // note l'identifiant pour ne pas signaler la seconde comme une anomalie.
      this.answered.set(msg.requestId, Date.now());
      // Purge ici aussi : la seule autre purge est dans la branche des réponses tardives, qui
      // peut ne jamais s'exécuter — la table grossissait alors pour toute la séance.
      this.pruneAnswered();
      pending.resolve(msg);
      return;
    }

    // Handle state updates
    if (msg.type === 'event' && msg.action === 'stateUpdate' && msg.payload) {
      this._lastState = msg.payload as unknown as TradingState;
      for (const handler of this.stateHandlers) {
        try { handler(this._lastState); }
        catch (e) { log.fail('Wire', e, 'A state handler threw — the deck may be showing stale data'); }
      }
      return;
    }

    if (msg.type === 'response' && msg.requestId) {
      if (this.answered.has(msg.requestId)) {
        // Seconde réponse attendue : NinjaTrader confirme une action déjà acquittée localement.
        // Ce n'est pas une anomalie — la signaler en WARN masquerait les vraies réponses tardives.
        log.debugEvent('Wire', `Confirmation NT8 pour ${msg.action}`, {
          req: msg.requestId,
          source: msg.source,
          error: msg.error ? `${msg.error.code}: ${msg.error.message}` : undefined,
        });
      } else {
        // Personne n'attend celle-ci : la requête avait expiré. La touche a renoncé avant que
        // la réponse n'arrive, ce qui mérite d'être su.
        log.eventWarn('Wire', `Réponse tardive ou sans correspondance pour ${msg.action}`, {
          req: msg.requestId,
          error: msg.error ? `${msg.error.code}: ${msg.error.message}` : undefined,
        });
      }
      this.pruneAnswered();
    }

    // Generic message handlers
    for (const handler of this.messageHandlers) {
      try { handler(msg); }
      catch (e) { log.fail('Wire', e, `A message handler threw while processing ${msg.type}/${msg.action}`); }
    }
  }

  private scheduleReconnect(): void {
    if (!this.running) return;
    log.debugEvent('Connection', `Reconnecting in ${this.reconnectDelay}ms`);
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
      try { handler(connected); }
      catch (e) { log.fail('Connection', e, `A connection handler threw (connected=${connected})`); }
    }
  }
}
