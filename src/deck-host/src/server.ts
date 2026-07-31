/**
 * Serveur de l'interface de configuration.
 *
 * HTTP pour la page, WebSocket pour l'état en direct et les modifications de layout. Écoute
 * strictement sur 127.0.0.1 : cette interface pilote l'envoi d'ordres, elle n'a rien à faire
 * sur le réseau.
 */
import { createServer, IncomingMessage, ServerResponse } from 'http';
import { WebSocketServer, WebSocket } from 'ws';
import { readFileSync, existsSync } from 'fs';
import { join, extname, dirname, normalize } from 'path';
import { fileURLToPath } from 'url';
import { CATALOG } from './catalog.js';
import { Layout, LayoutStore } from './layout.js';
import * as log from './logger.js';

const UI_DIR = join(dirname(fileURLToPath(import.meta.url)), '..', 'ui');

const MIME: Record<string, string> = {
  '.html': 'text/html; charset=utf-8',
  '.css': 'text/css; charset=utf-8',
  '.js': 'text/javascript; charset=utf-8',
  '.svg': 'image/svg+xml',
  '.png': 'image/png',
};

/** Ce que l'interface affiche en direct — un aperçu fidèle de ce qui est sur le boîtier. */
export interface UiSnapshot {
  deviceConnected: boolean;
  deviceName: string;
  columns: number;
  rows: number;
  bridgeConnected: boolean;
  ntConnected: boolean;
  currentPage: number;
  /** Aperçu de chaque touche, en data URI SVG — l'interface les affiche telles quelles. */
  previews: Record<string, string>;
  state: Record<string, unknown> | null;
}

export class ConfigServer {
  #http = createServer((req, res) => this.#serve(req, res));
  #wss = new WebSocketServer({ noServer: true });
  #clients = new Set<WebSocket>();
  #store: LayoutStore;
  #snapshot: () => UiSnapshot;
  #onPageChange: (page: number) => void;

  constructor(store: LayoutStore, snapshot: () => UiSnapshot, onPageChange: (page: number) => void) {
    this.#store = store;
    this.#snapshot = snapshot;
    this.#onPageChange = onPageChange;

    this.#http.on('upgrade', (req, socket, head) => {
      this.#wss.handleUpgrade(req, socket as any, head, (ws) => this.#attach(ws));
    });
  }

  listen(port: number): Promise<string> {
    return new Promise((resolve, reject) => {
      this.#http.once('error', reject);
      this.#http.listen(port, '127.0.0.1', () => {
        const url = `http://127.0.0.1:${port}`;
        log.event('Server', 'Interface de configuration disponible', { url });
        resolve(url);
      });
    });
  }

  #attach(ws: WebSocket): void {
    this.#clients.add(ws);
    ws.on('close', () => this.#clients.delete(ws));
    ws.on('message', (raw) => {
      let msg: any;
      try {
        msg = JSON.parse(String(raw));
      } catch {
        return;
      }
      try {
        this.#handle(msg);
      } catch (err) {
        log.fail('Server', err, 'Message d\'interface invalide', { type: msg?.type });
      }
    });

    // Tout ce dont l'interface a besoin pour se dessiner, en un envoi.
    ws.send(JSON.stringify({
      type: 'hello',
      catalog: CATALOG,
      layout: this.#store.layout,
      layoutPath: this.#store.path,
      snapshot: this.#snapshot(),
    }));
  }

  #handle(msg: any): void {
    switch (msg.type) {
      case 'saveLayout':
        this.#store.update(msg.layout as Layout);
        this.broadcastLayout();
        break;
      case 'setPage':
        this.#onPageChange(Number(msg.page) || 0);
        break;
      default:
        log.debugEvent('Server', 'Message d\'interface inconnu', { type: msg.type });
    }
  }

  broadcastLayout(): void {
    this.#send({ type: 'layout', layout: this.#store.layout });
  }

  /** Appelé à chaque rafraîchissement d'état : l'aperçu de l'interface suit le boîtier. */
  broadcastSnapshot(): void {
    if (this.#clients.size === 0) return;
    this.#send({ type: 'snapshot', snapshot: this.#snapshot() });
  }

  #send(payload: unknown): void {
    const data = JSON.stringify(payload);
    for (const ws of this.#clients) {
      if (ws.readyState === ws.OPEN) ws.send(data);
    }
  }

  #serve(req: IncomingMessage, res: ServerResponse): void {
    const rel = (req.url || '/').split('?')[0];
    const name = rel === '/' ? 'index.html' : rel.replace(/^\//, '');
    // Empêche de sortir du dossier ui/ via des ../ dans l'URL.
    const file = normalize(join(UI_DIR, name));
    if (!file.startsWith(normalize(UI_DIR)) || !existsSync(file)) {
      res.writeHead(404, { 'Content-Type': 'text/plain; charset=utf-8' });
      res.end('Introuvable');
      return;
    }
    res.writeHead(200, { 'Content-Type': MIME[extname(file)] ?? 'application/octet-stream' });
    res.end(readFileSync(file));
  }

  close(): void {
    for (const ws of this.#clients) ws.terminate();
    this.#http.close();
  }
}
