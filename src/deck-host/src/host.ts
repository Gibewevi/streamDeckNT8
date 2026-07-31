/**
 * Hôte deck — point d'entrée.
 *
 * Remplace le couple « application Stream Deck + plugin ». Le bridge et l'add-on NT8 sont
 * inchangés : ce processus est un client du bridge, exactement comme l'était le plugin.
 *
 *   boîtier USB ──► DeckDevice ──► host ──ws://127.0.0.1:8218──► bridge ──► add-on NT8
 *                                   │
 *                                   └──► ConfigServer ──► interface de configuration
 */
import { BridgeClient } from './bridge-client.js';
import {
  DEFAULT_GLOBAL_SETTINGS, DEFAULT_SAFETY_STATUS, TradingState, OrderUpdate, SafetyStatus, createCommand,
} from './messages.js';
import { DeckDevice } from './device.js';
import { LayoutStore, SlotAssignment } from './layout.js';
import { computeVisual, VisualContext } from './visual-engine.js';
import { renderButtonSvg } from './visuals.js';
import { ConfigServer, UiSnapshot } from './server.js';
import { BridgeSupervisor } from './supervisor.js';
import { actionName } from './catalog.js';
import * as log from './logger.js';

const VERSION = '0.1.0';
const UI_PORT = Number(process.env.DECKHOST_UiPort ?? 8220);
const BRIDGE_URL = process.env.DECKHOST_BridgeUrl ?? DEFAULT_GLOBAL_SETTINGS.bridgeUrl;
const BRIDGE_PORT = Number(new URL(BRIDGE_URL).port || 8218);

log.installProcessHandlers();
log.logSessionHeader(VERSION);

const DISCONNECTED_STATE: TradingState = {
  account: '', instrument: '', quantity: 1, defaultQuantity: 1,
  ntConnected: false, pluginConnected: false, position: null, instrumentInfo: null,
  availableAccounts: [], cooldownEnabled: false, cooldownActive: false,
  cooldownSecondsRemaining: 0, safety: { ...DEFAULT_SAFETY_STATUS },
};

const bridge = new BridgeClient(BRIDGE_URL);
const device = new DeckDevice();
const store = new LayoutStore();
const supervisor = new BridgeSupervisor(BRIDGE_PORT);

let lastState: TradingState | null = null;
let lastRejectionAt: number | null = null;
let currentPage = 0;
let cooldownTimer: ReturnType<typeof setInterval> | null = null;

function ctx(): VisualContext {
  return {
    bridgeConnected: bridge.isConnected,
    lastRejectionAt,
    defaultQuantity: DEFAULT_GLOBAL_SETTINGS.defaultQuantity,
  };
}

function page() {
  const pages = store.layout.pages;
  return pages[Math.min(currentPage, pages.length - 1)] ?? { name: '', slots: {} };
}

function slotAt(index: number): SlotAssignment | null {
  return page().slots[String(index)] ?? null;
}

// --- Rendu ---

function visualFor(index: number) {
  const assignment = slotAt(index);
  if (!assignment) return null;
  return computeVisual(assignment.actionId, assignment.settings ?? {}, lastState ?? DISCONNECTED_STATE, ctx());
}

// `paintAll` est déclenché depuis six endroits (état du bridge, connexion, édition du layout,
// appui, tic de cooldown, changement de page). Sans coalescence, deux passes concurrentes lisent
// toutes deux `#painted` avant que l'une n'écrive : la même touche part deux ou trois fois sur
// l'USB, ce qui annule le bénéfice du rendu différentiel.
let painting = false;
let repaintPending = false;

async function paintAll(): Promise<void> {
  if (painting) {
    repaintPending = true;
    return;
  }
  painting = true;
  try {
    do {
      repaintPending = false;
      if (!device.connected) break;
      const count = device.keyCount;
      for (let i = 0; i < count; i++) {
        await device.paint(i, visualFor(i));
      }
      log.traceEvent('Visual', 'Deck rafraîchi', { page: currentPage, keys: count });
    } while (repaintPending);
  } finally {
    painting = false;
  }
}

/** Aperçu envoyé à l'interface : les mêmes visuels, en SVG, sans passer par le matériel. */
function snapshot(): UiSnapshot {
  const previews: Record<string, string> = {};
  const count = device.connected ? device.keyCount : store.layout.device.columns * store.layout.device.rows;
  for (let i = 0; i < count; i++) {
    const v = visualFor(i);
    if (v) previews[String(i)] = renderButtonSvg(v);
  }
  return {
    deviceConnected: device.connected,
    deviceName: device.productName,
    columns: device.connected ? device.columns : store.layout.device.columns,
    rows: device.connected ? device.rows : store.layout.device.rows,
    bridgeConnected: bridge.isConnected,
    ntConnected: lastState?.ntConnected ?? false,
    currentPage,
    previews,
    state: lastState as unknown as Record<string, unknown> | null,
  };
}

const server = new ConfigServer(store, snapshot, (p) => {
  currentPage = Math.max(0, Math.min(p, store.layout.pages.length - 1));
  device.invalidate();
  void paintAll();
  server.broadcastSnapshot();
});

store.onChange(() => {
  // Le layout a changé : les emplacements ne correspondent plus, tout est à redessiner.
  device.invalidate();
  device.setBrightness(store.layout.brightness);
  void paintAll();
  server.broadcastSnapshot();
  // Les limites de sécurité vivent dans le bridge, pas dans le layout : il faut les lui repousser
  // à chaque édition, sinon l'interface afficherait des valeurs que le bridge ignore.
  void syncSafetyConfig();
});

// --- Appuis ---

async function sendCmd(action: string, payload: Record<string, unknown>, settings: Record<string, unknown>): Promise<void> {
  const selectedAccount = lastState?.ntConnected ? (lastState.account || '').trim() : '';
  const fallbackAccount = typeof settings.account === 'string' ? settings.account.trim() : '';
  const account = selectedAccount || fallbackAccount || DEFAULT_GLOBAL_SETTINGS.defaultAccount;
  const instrument = (settings.instrument as string) || lastState?.instrument || DEFAULT_GLOBAL_SETTINGS.defaultInstrument;
  const cmd = createCommand(action, { account, instrument, ...payload });

  const startedAt = Date.now();
  log.event('Command', `Envoi de ${action}`, { req: cmd.requestId ?? '', account, instrument, payload });

  const resp = await bridge.sendCommand(cmd);
  const elapsedMs = Date.now() - startedAt;

  if (resp.error) {
    // Les refus sont la façon normale dont le bridge applique sécurité, temporisation et
    // disponibilité de NT8 : ce sont des événements attendus, pas des pannes.
    log.eventWarn('Command', `${action} refusé`, {
      req: cmd.requestId ?? '', code: resp.error.code, reason: resp.error.message, elapsedMs,
    });
    throw new Error(`${resp.error.code}: ${resp.error.message}`);
  }
  log.event('Command', `${action} accepté`, { req: cmd.requestId ?? '', elapsedMs });
}

/**
 * Correspondance action → commande du bridge.
 *
 * Les noms sont ceux de `MessageValidator.KnownActions` (`src/StreamDeckBridge/
 * MessageValidator.cs:15-23`) : tout autre nom est rejeté avec `UNKNOWN_ACTION`.
 *
 * Deux distinctions à ne jamais confondre, elles portent sur de l'argent réel :
 *  - `cancelOrders` annule les ordres en attente **et ferme la position** (touche « Close All ») ;
 *  - `cancelWorkingOrders` n'annule que les ordres en attente, la position reste ouverte.
 */
const COMMANDS: Record<string, (s: Record<string, unknown>, qty: number) => [string, Record<string, unknown>]> = {
  'com.trader.ninjatrader.buymarket': (_s, qty) => ['buyMarket', { quantity: qty }],
  'com.trader.ninjatrader.sellmarket': (_s, qty) => ['sellMarket', { quantity: qty }],
  // Les décalages par défaut sont signés : l'achat limite se place sous le marché, la vente au-dessus.
  'com.trader.ninjatrader.buylimit': (s, qty) => ['buyLimit', { quantity: qty, offsetTicks: (s.offsetTicks as number) ?? -2 }],
  'com.trader.ninjatrader.selllimit': (s, qty) => ['sellLimit', { quantity: qty, offsetTicks: (s.offsetTicks as number) ?? 2 }],
  'com.trader.ninjatrader.flatten': () => ['flatten', {}],
  'com.trader.ninjatrader.cancelorders': () => ['cancelOrders', {}],
  'com.trader.ninjatrader.cancelworkingorders': () => ['cancelWorkingOrders', {}],
  'com.trader.ninjatrader.reverse': () => ['reverse', {}],
  'com.trader.ninjatrader.breakeven': (s) => ['breakeven', { offsetTicks: (s.offsetTicks as number) ?? 0 }],
  'com.trader.ninjatrader.stopplus': () => ['moveStop', { deltaTicks: 1 }],
  'com.trader.ninjatrader.stopminus': () => ['moveStop', { deltaTicks: -1 }],
  'com.trader.ninjatrader.targetplus': () => ['moveTarget', { deltaTicks: 1 }],
  'com.trader.ninjatrader.targetminus': () => ['moveTarget', { deltaTicks: -1 }],
  // BE± déplace bien le stop : il n'existe pas de commande de break-even incrémentale côté bridge.
  'com.trader.ninjatrader.beplus': () => ['moveStop', { deltaTicks: 1 }],
  'com.trader.ninjatrader.beminus': () => ['moveStop', { deltaTicks: -1 }],
};

/** Adopte l'état de sécurité que le bridge joint à chacune de ses réponses. */
function applySafetyResponse(resp: { payload?: Record<string, unknown> }): void {
  const safety = resp.payload as unknown as SafetyStatus | undefined;
  if (!safety || typeof safety.armed !== 'boolean') return;
  if (!lastState) lastState = { ...DISCONNECTED_STATE };
  lastState.safety = { ...DEFAULT_SAFETY_STATUS, ...safety };
}

function normalizeAccountList(value: unknown): string[] {
  const rawItems = Array.isArray(value) ? value : typeof value === 'string' ? value.split(/[\n,;]/) : [];
  const seen = new Set<string>();
  const accounts: string[] = [];
  for (const item of rawItems) {
    const account = typeof item === 'string' ? item.trim() : '';
    if (!account) continue;
    const key = account.toUpperCase();
    if (seen.has(key)) continue;
    seen.add(key);
    accounts.push(account);
  }
  return accounts;
}

/**
 * Les réglages ne font que réordonner ou filtrer les comptes actuellement actifs dans NT8.
 * Ils ne réintroduisent jamais un compte que NinjaTrader ne publie plus.
 */
function getAccountCycleList(settings: Record<string, unknown>, state: TradingState | null): string[] {
  const available = normalizeAccountList(state?.availableAccounts ?? []);
  if (available.length === 0) return [];
  const configured = normalizeAccountList(settings.accounts);
  if (configured.length === 0) return available;

  const byName = new Map(available.map((a) => [a.toUpperCase(), a]));
  const kept = configured.map((a) => byName.get(a.toUpperCase())).filter((a): a is string => Boolean(a));
  return kept.length > 0 ? kept : available;
}

/**
 * Pousse les limites de sécurité vers le bridge, qui les possède et les persiste.
 * Un refus pendant que la macro est armée est le comportement attendu : les limites verrouillées
 * restent en vigueur.
 */
async function pushSafetyConfig(settings: Record<string, unknown>): Promise<void> {
  const payload: Record<string, unknown> = {};
  for (const key of ['maxTradesWhenLosing', 'dailyLossLimit', 'lockDurationHours']) {
    const value = settings[key];
    if (typeof value === 'number' && Number.isFinite(value)) payload[key] = value;
  }
  if (Object.keys(payload).length === 0 || !bridge.isConnected) return;

  const resp = await bridge.sendCommand(createCommand('configureSafety', payload));
  if (resp.error) {
    log.event('Safety', 'configureSafety refusé par le bridge', { code: resp.error.code, reason: resp.error.message, requested: payload });
  } else {
    log.event('Safety', 'Limites de sécurité poussées vers le bridge', { requested: payload });
  }
  applySafetyResponse(resp);
}

/** Ce que fait chaque action à l'appui. Miroir des 23 handlers de `plugin.ts:615-820`. */
async function runAction(assignment: SlotAssignment): Promise<void> {
  const s = assignment.settings ?? {};
  const qty = lastState?.quantity ?? DEFAULT_GLOBAL_SETTINGS.defaultQuantity;
  const id = assignment.actionId;

  // Navigation : action propre à l'hôte, aucun aller-retour vers le bridge.
  if (id === 'host.navigate') {
    const total = store.layout.pages.length;
    const cible = String(s.targetPage ?? 'next');
    // Suivante et précédente bouclent : sur un deck, une touche de navigation qui ne fait plus
    // rien en bout de liste passe pour une touche cassée.
    if (cible === 'next') currentPage = (currentPage + 1) % total;
    else if (cible === 'prev') currentPage = (currentPage - 1 + total) % total;
    else currentPage = Math.min(Math.max(0, Number(cible) - 1), total - 1);

    log.event('Navigation', 'Changement de page', { vers: currentPage + 1, sur: total, cible });
    device.invalidate();
    await paintAll();
    server.broadcastSnapshot();
    return;
  }

  const simple = COMMANDS[id];
  if (simple) {
    const [action, payload] = simple(s, qty);
    await sendCmd(action, payload, s);
    return;
  }

  switch (id) {
    // Quantité — l'état local est repris de la réponse, pour un retour visuel immédiat
    // sans attendre la prochaine diffusion du bridge.
    case 'com.trader.ninjatrader.qtyplus':
    case 'com.trader.ninjatrader.qtyminus':
    case 'com.trader.ninjatrader.qtyreset': {
      const isReset = id.endsWith('qtyreset');
      const cmd = isReset
        ? createCommand('qtyReset', {})
        : createCommand('qtyAdjust', { delta: id.endsWith('qtyplus') ? 1 : -1 });
      const resp = await bridge.sendCommand(cmd);
      if (resp.error) throw new Error(`${resp.error.code}: ${resp.error.message}`);
      if (resp.result?.success && lastState) {
        lastState.quantity = (resp.result as { quantity?: number }).quantity ?? lastState.quantity;
      }
      return;
    }

    case 'com.trader.ninjatrader.instrument': {
      const instrument = (s.instrument as string) || '';
      if (!instrument) return; // Non configuré : ne rien envoyer.
      if (!lastState) lastState = { ...DISCONNECTED_STATE };
      lastState.instrument = instrument;
      if (bridge.isConnected) {
        const resp = await bridge.sendCommand(createCommand('setInstrument', { instrument }));
        if (resp.error) throw new Error(`${resp.error.code}: ${resp.error.message}`);
      }
      return;
    }

    case 'com.trader.ninjatrader.account': {
      const accounts = getAccountCycleList(s, lastState);
      if (accounts.length === 0) {
        log.eventWarn('Account', 'Touche Compte pressée mais NinjaTrader ne publie aucun compte actif', {
          ntConnected: lastState?.ntConnected ?? false, configured: s.accounts,
        });
        return;
      }
      const current = lastState?.ntConnected ? (lastState.account ?? '') : '';
      const nextIdx = (accounts.indexOf(current) + 1) % accounts.length;
      const next = accounts[nextIdx];
      log.event('Account', 'Changement de compte', {
        from: current || '(aucun)', to: next, index: `${nextIdx + 1}/${accounts.length}`, available: accounts.join(','),
      });
      if (!lastState) lastState = { ...DISCONNECTED_STATE };
      lastState.account = next;
      if (bridge.isConnected) {
        const resp = await bridge.sendCommand(createCommand('setAccount', { account: next }));
        if (resp.error) throw new Error(`${resp.error.code}: ${resp.error.message}`);
      }
      return;
    }

    // Affichage seul : l'appui ne fait que redemander l'état.
    case 'com.trader.ninjatrader.status':
      bridge.send(createCommand('getState', {}));
      return;

    case 'com.trader.ninjatrader.cooldown': {
      const resp = await bridge.sendCommand(createCommand('toggleCooldown', {}));
      if (resp.error) throw new Error(`${resp.error.code}: ${resp.error.message}`);
      return;
    }

    // Arme la macro, ou tente de la désarmer une fois le verrou expiré. Un refus pendant le
    // verrou est le comportement attendu, par conception.
    case 'com.trader.ninjatrader.safety': {
      const resp = await bridge.sendCommand(createCommand('toggleSafety', {}));
      applySafetyResponse(resp);
      if (resp.error) {
        log.eventWarn('Safety', 'toggleSafety refusé (verrou toujours actif)', { code: resp.error.code, reason: resp.error.message });
        throw new Error(`${resp.error.code}: ${resp.error.message}`);
      }
      log.event('Safety', 'Macro de sécurité basculée', {
        armed: lastState?.safety?.armed ?? false,
        lockSecondsRemaining: lastState?.safety?.lockSecondsRemaining ?? 0,
      });
      return;
    }

    default:
      log.eventWarn('KeyDown', 'Action inconnue', { actionId: id });
  }
}

/** Rejoue la configuration de sécurité présente dans le layout — à la connexion et après édition. */
async function syncSafetyConfig(): Promise<void> {
  for (const p of store.layout.pages) {
    for (const a of Object.values(p.slots)) {
      if (a.actionId === 'com.trader.ninjatrader.safety') {
        await pushSafetyConfig(a.settings ?? {});
        return;
      }
    }
  }
}

device.onPress(async (ev) => {
  const assignment = slotAt(ev.index);
  if (!assignment) {
    log.traceEvent('KeyDown', 'Appui sur un emplacement vide', { slot: ev.index });
    return;
  }

  // Chaque appui est journalisé avant toute tentative : une touche qui n'a produit aucun ordre
  // et une touche dont l'ordre a été refusé se ressemblent sur le deck, et seule cette ligne
  // permet de les distinguer après coup.
  const startedAt = Date.now();
  log.event('KeyDown', `${actionName(assignment.actionId)} pressée`, {
    slot: ev.index, actionId: assignment.actionId,
    qty: lastState?.quantity ?? '', instrument: lastState?.instrument ?? '',
    account: lastState?.account ?? '', settings: assignment.settings,
  });

  try {
    await runAction(assignment);
    log.event('KeyDown', `${actionName(assignment.actionId)} terminée`, { elapsedMs: Date.now() - startedAt });
  } catch (err) {
    log.fail('KeyDown', err, `${actionName(assignment.actionId)} a échoué`, { elapsedMs: Date.now() - startedAt });
  }
  await paintAll();
  server.broadcastSnapshot();
});

device.onConnectionChange((connected) => {
  log.event('Device', connected ? 'Boîtier disponible' : 'Boîtier indisponible');
  if (connected) {
    device.setBrightness(store.layout.brightness);
    void paintAll();
  }
  server.broadcastSnapshot();
});

// --- État venant du bridge ---

bridge.onStateUpdate((state) => {
  lastState = state;
  if (state.cooldownActive) startCooldownTimer();
  void paintAll();
  server.broadcastSnapshot();
});

bridge.onConnectionChange((connected) => {
  log.event('Connection', connected ? 'Bridge connecté' : 'Bridge déconnecté', { url: BRIDGE_URL });
  if (!connected) lastState = null;
  // Le bridge redémarre sans mémoire de nos limites : les repousser à chaque reconnexion.
  if (connected) void syncSafetyConfig();
  void paintAll();
  server.broadcastSnapshot();
});

bridge.onMessage((msg) => {
  if (msg.action === 'orderUpdate' && msg.payload) {
    const update = msg.payload as unknown as OrderUpdate;
    if (update.rejected) {
      lastRejectionAt = Date.now();
      log.eventWarn('Order', 'Ordre rejeté par NinjaTrader', { reason: update.reason || update.error });
      void paintAll();
      // La bannière « REJECTED » doit s'effacer d'elle-même.
      setTimeout(() => void paintAll(), 5200);
    }
  }
});

/** Le compte à rebours s'égrène localement, pour un affichage fluide entre deux états du bridge. */
function startCooldownTimer(): void {
  if (cooldownTimer) return;
  cooldownTimer = setInterval(() => {
    if (!lastState?.cooldownActive || lastState.cooldownSecondsRemaining <= 0) {
      clearInterval(cooldownTimer!);
      cooldownTimer = null;
      if (lastState) lastState.cooldownActive = false;
      void paintAll();
      return;
    }
    lastState.cooldownSecondsRemaining = Math.max(0, lastState.cooldownSecondsRemaining - 1);
    void paintAll();
    server.broadcastSnapshot();
  }, 1000);
}

// --- Démarrage ---

async function main(): Promise<void> {
  let url: string;
  try {
    url = await server.listen(UI_PORT);
  } catch (err) {
    // Port occupé = une autre instance tourne presque toujours. Deux hôtes se disputeraient le
    // boîtier et la socket plugin du bridge : mieux vaut céder la place immédiatement, avec un
    // message lisible. La tâche planifiée relance en boucle — une trace de pile n'y aiderait pas.
    if ((err as NodeJS.ErrnoException).code === 'EADDRINUSE') {
      log.eventWarn('Session', 'Un autre hôte occupe déjà le port — arrêt de cette instance', {
        port: UI_PORT, conseil: `ouvrez http://127.0.0.1:${UI_PORT}`,
      });
      process.exit(0);
    }
    throw err;
  }
  // Avant de connecter le client : sans l'application Elgato, plus personne d'autre ne démarre
  // le bridge.
  await supervisor.start();
  await device.start();
  bridge.start();
  await paintAll();

  log.event('Session', 'Hôte prêt', { ui: url, bridge: BRIDGE_URL, layout: store.path });
  process.stdout.write(`\n  Interface de configuration : ${url}\n\n`);
}

async function shutdown(signal: string): Promise<void> {
  log.event('Session', 'Arrêt demandé', { signal });
  supervisor.stop();
  server.close();
  await device.close();
  process.exit(0);
}

process.on('SIGINT', () => void shutdown('SIGINT'));
process.on('SIGTERM', () => void shutdown('SIGTERM'));

void main();
