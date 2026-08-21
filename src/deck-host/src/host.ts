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
  DEFAULT_GLOBAL_SETTINGS, DEFAULT_SAFETY_STATUS, DISCONNECTED_STATE, TradingState, OrderUpdate, GuardViolation, SafetyStatus, createCommand,
  parseFollowers, formatFollowers,
} from './messages.js';
import { DeckDevice } from './device.js';
import { DEFAULT_DATA_DIR, LayoutStore, SlotAssignment } from './layout.js';
import { existsSync, mkdirSync, readFileSync, writeFileSync } from 'fs';
import { dirname, join } from 'path';
import { computeVisual, gainEnTicks, tiltAppliesTo, VIOLATION_BANNER_MS, VisualContext } from './visual-engine.js';
import { renderButtonDataUri } from './render-node.js';
import { ConfigServer, UiSnapshot } from './server.js';
import { BridgeSupervisor, neutraliserElgato } from './supervisor.js';
import { empreinteDe, journaliserTransitions, journaliserConfiguration, Empreinte } from './transitions.js';
import { actionName, CATALOG_BY_ID, HOLD_CONFIRM_MS } from './catalog.js';
import { BitlearnClient } from './bitlearn.js';
import { EventRecorder } from './journal.js';
import { JournalUploader, comptesJournalises } from './uploader.js';
import { etatAddOn, journaliserEtat, localiserNinjaScript } from './ninjatrader.js';
import { hostname } from 'os';
import * as log from './logger.js';

const VERSION = '0.28.0';
const UI_PORT = Number(process.env.DECKHOST_UiPort ?? 8220);
const BRIDGE_URL = process.env.DECKHOST_BridgeUrl ?? DEFAULT_GLOBAL_SETTINGS.bridgeUrl;
const BRIDGE_PORT = Number(new URL(BRIDGE_URL).port || 8218);

log.installProcessHandlers();
log.logSessionHeader(VERSION);

const bridge = new BridgeClient(BRIDGE_URL);
const device = new DeckDevice();
const store = new LayoutStore();
const supervisor = new BridgeSupervisor(BRIDGE_PORT);
const bitlearn = new BitlearnClient();
const journal = new EventRecorder();
// La clé de scellement arrive avec le jeton à l'appairage, ou au battement pour un poste appairé
// avant que le scellement n'existe — une seule fois dans les deux cas. L'add-on la relira dans le
// dossier du journal : c'est le seul terrain que les deux processus partagent, et il y écrit déjà.
bitlearn.onJournalKey((cle) => journal.installerCle(cle));
const uploader = new JournalUploader(
  bitlearn,
  () => comptesJournalises(store.layout),
  // Relu à chaque envoi, jamais figé au démarrage : un capital capturé une fois pour toutes serait
  // faux dès le premier trade, et un chiffre faux est pire qu'un chiffre absent.
  () => {
    const compte = lastState?.account?.trim();
    if (!compte) return null;

    // Le solde frais s'il est là, le dernier connu sinon. NinjaTrader ne le publie pas à chaque
    // instant — il manque à la reconnexion, pendant la fenêtre de garde du bridge, et dès que la
    // liaison hoquette. Exiger qu'il soit présent à la seconde précise de l'envoi revenait à le
    // perdre presque toujours, et le journal restait à zéro.
    //
    // Se rabattre sur une valeur d'il y a quelques minutes ne fausse rien : Bitlearn calcule
    // `capital de départ = solde − P&L cumulé`, une soustraction qui se corrige d'elle-même au
    // passage suivant. C'est un capital FIGÉ au démarrage qu'il fallait éviter, pas un solde
    // légèrement en retard.
    const frais = lastState?.cashValue;
    if (typeof frais === 'number' && Number.isFinite(frais)) {
      soldesConnus.set(compte, frais);
      return { compte, solde: frais };
    }

    const memorise = soldesConnus.get(compte);
    return typeof memorise === 'number' ? { compte, solde: memorise } : null;
  },
);

/**
 * Dernier solde connu par compte.
 *
 * Vit dans l'hôte et non dans le layout : c'est une observation, pas un réglage. Perdu au
 * redémarrage, ce qui est sans conséquence — NinjaTrader le republie dès qu'il se reconnecte.
 */
const soldesConnus = new Map<string, number>();

let lastState: TradingState | null = null;
let lastRejectionAt: number | null = null;
/** Dernier ordre manuel refusé par l'add-on pendant un blocage de la macro. */
let lastViolationAt: number | null = null;
let currentPage = 0;
let cooldownTimer: ReturnType<typeof setInterval> | null = null;

function ctx(): VisualContext {
  return {
    bridgeConnected: bridge.isConnected,
    lastRejectionAt,
    lastViolationAt,
    defaultQuantity: DEFAULT_GLOBAL_SETTINGS.defaultQuantity,
    autoBe: { actif: autoBe.actif, pose: autoBe.pose !== null },
    autoTpSl: { actif: autoTpSl.actif, pose: autoTpSl.pose !== null },
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
  const visual = computeVisual(assignment.actionId, assignment.settings ?? {}, lastState ?? DISCONNECTED_STATE, ctx());
  if (!visual) return null;
  // La jauge de maintien se superpose au visuel calculé, sans le remplacer : le trader doit
  // continuer à lire ce que fait la touche pendant qu'il la tient.
  const p = progressionDe(index);
  if (p === undefined) return visual;

  // Sur un maintien long — celui qu'impose l'Anti-Tilt — la jauge n'avance que de quelques pixels
  // par seconde et paraît figée. Le décompte est ce qui dit au trader que son appui est bien pris,
  // et combien de temps il lui reste à tenir.
  const restant = restantDe(index);
  return restant === undefined ? { ...visual, progress: p } : { ...visual, progress: p, subtitle: `${restant}s` };
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
    if (v) previews[String(i)] = renderButtonDataUri(v);
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
  // Limites de sécurité et durée de temporisation vivent dans le bridge, pas dans le layout : il
  // faut les lui repousser à chaque édition, sinon l'interface afficherait des valeurs qu'il ignore.
  void syncConfig();
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
 * Les comptes que la touche fait défiler : ceux que NinjaTrader publie, ni plus ni moins.
 * Elle ne réintroduit jamais un compte que la plateforme ne publie plus.
 */
function getAccountCycleList(state: TradingState | null): string[] {
  const base = normalizeAccountList(state?.availableAccounts ?? []);
  if (base.length === 0) return [];

  // Le réglage `accounts` n'est PLUS lu, et ce n'est pas un oubli : il a été retiré du catalogue,
  // et un réglage retiré de l'écran qui continuerait d'agir serait le pire des deux mondes — un
  // défilement restreint par une liste que plus personne ne voit ni ne peut corriger. Une clé
  // résiduelle dans un layout ancien est donc inerte.
  //
  // Les comptes du groupe de copie ne sont PAS exclus, et c'est un renversement voulu. Ils
  // l'étaient tant que la liste décrivait des « suiveurs » : sélectionner l'un d'eux en aurait
  // fait un maître qui se copiait vers ses propres pairs. La liste décrit désormais un groupe
  // dont le maître fait partie, et les suiveurs effectifs s'en déduisent — `syncCopierConfig`
  // retire le compte sélectionné avant d'envoyer. Passer d'un membre à l'autre est donc devenu
  // le geste normal : c'est lui qui échange les rôles.
  return base;
}

/**
 * Pousse les limites de sécurité vers le bridge, qui les possède et les persiste.
 * Un refus pendant que la macro est armée est le comportement attendu : les limites verrouillées
 * restent en vigueur.
 */
async function pushSafetyConfig(settings: Record<string, unknown>): Promise<void> {
  const payload: Record<string, unknown> = {};
  const nombre = (key: string): number | null => {
    const value = settings[key];
    return typeof value === 'number' && Number.isFinite(value) ? value : null;
  };

  for (const key of ['maxTradesWhenLosing', 'dailyLossLimit', 'maxContracts', 'lockDurationHours']) {
    const value = nombre(key);
    if (value !== null) payload[key] = value;
  }
  // Les bascules ne sont transmises que si elles existent dans le layout : une absence doit laisser
  // le défaut du bridge en place, et non être lue comme un « false » que personne n'a demandé.
  for (const key of ['antiTiltEnabled', 'tiltAveragingAllowed', 'tiltAdvanced', 'autoFlattenOnDailyLoss']) {
    const value = settings[key];
    if (typeof value === 'boolean') payload[key] = value;
  }

  // La tolérance n'a de sens qu'avec la liquidation active. Hors de là, ne pas la transmettre
  // laisse le bridge sur sa valeur, sans effacer ce qui avait été saisi.
  if (settings.autoFlattenOnDailyLoss === true) {
    const value = nombre('autoFlattenGraceSeconds');
    if (value !== null) payload.autoFlattenGraceSeconds = value;
  }

  // Les durées avancées : hors de la section, elles ne sont pas transmises et le bridge
  // applique ses valeurs par défaut — sans rien effacer de ce qui avait été saisi.
  if (settings.tiltAdvanced === true) {
    for (const key of ['tiltHoldSeconds', 'tiltEpisodeMinutes']) {
      const value = nombre(key);
      if (value !== null) payload[key] = value;
    }
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

  // Auto BE : bascule un automatisme local, aucun ordre envoyé à l'appui lui-même.
  if (id === 'host.autobe') {
    autoBe.actif = !autoBe.actif;
    // Réarmer à l'activation : un break-even posé lors d'une session précédente ne doit pas
    // empêcher la pose sur la position en cours.
    autoBe.pose = null;
    autoBe.echecs = 0;
    log.event('AutoBE', autoBe.actif ? 'Automatisme ARMÉ' : 'Automatisme DÉSARMÉ', {
      seuilTicks: Number(s.triggerTicks) || 0, offsetTicks: Number(s.offsetTicks) || 0,
    });
    persisterArmementAutoBe();
    if (autoBe.actif && lastState) evaluerAutoBe(lastState);
    return;
  }

  // Auto TP/SL : même nature que l'Auto BE — bascule d'un automatisme local, aucun ordre à l'appui.
  if (id === 'host.autotpsl') {
    const { tp, sl } = distancesTpSl(s);

    // Armer une macro qui n'a aucune distance à poser produirait une touche orange annonçant une
    // protection qui n'existe pas. Le refus est le service rendu : c'est le même piège que la
    // Tendance armable sans autorisation de blocage.
    if (!autoTpSl.actif && tp === 0 && sl === 0) {
      log.eventWarn('AutoTPSL', 'Armement refusé — aucune distance réglée sur la touche', {
        correction: 'renseigner un Take Profit et/ou un Stop Loss dans les réglages de la touche',
      });
      return;
    }

    autoTpSl.actif = !autoTpSl.actif;
    // Réarmer à l'activation : un bracket posé lors d'une position précédente ne doit pas empêcher
    // la pose sur celle en cours.
    reinitialiserAutoTpSl();
    log.event('AutoTPSL', autoTpSl.actif ? 'Automatisme ARMÉ' : 'Automatisme DÉSARMÉ', {
      takeProfitTicks: tp, stopLossTicks: sl,
    });
    persisterArmementAutoTpSl();
    if (autoTpSl.actif && lastState) evaluerAutoTpSl(lastState);
    return;
  }

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
      const accounts = getAccountCycleList(lastState);
      if (accounts.length === 0) {
        log.eventWarn('Account', 'Touche Compte pressée mais NinjaTrader ne publie aucun compte actif', {
          ntConnected: lastState?.ntConnected ?? false,
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

        // Les rôles viennent de changer : le compte qui vient d'être sélectionné sort des
        // suiveurs, celui qu'on quitte y entre. La liste effective se recalcule ici et pas
        // seulement à la prochaine édition du layout — sans ce renvoi, le moteur continuerait
        // de copier vers le compte désormais maître, que le bridge refuserait.
        await syncCopierConfig();
      }
      return;
    }

    // Affichage seul : l'appui ne fait que redemander l'état.
    case 'com.trader.ninjatrader.status':
      bridge.send(createCommand('getState', {}));
      return;

    // Tendance. On n'arrive ici qu'au bout du maintien : `maintienArmementTendance` a imposé la
    // durée, et un appui plus court a été annulé sans rien envoyer. Sans autorisation de blocage,
    // la durée vaut 0 et la touche se contente de redemander l'état — elle reste un indicateur.
    case 'com.trader.ninjatrader.trend': {
      if (!lastState?.trend?.blockingAllowed) {
        bridge.send(createCommand('getState', {}));
        return;
      }
      const resp = await bridge.sendCommand(createCommand('toggleTrend', {}));
      if (resp.error) {
        log.eventWarn('Trend', 'toggleTrend refusé par le bridge', {
          code: resp.error.code, raison: resp.error.message,
        });
        throw new Error(`${resp.error.code}: ${resp.error.message}`);
      }
      const armed = (resp.result as { trendArmed?: boolean } | undefined)?.trendArmed === true;
      if (lastState?.trend) lastState.trend.armed = armed;
      log.event('Trend', armed ? 'Macro Tendance ARMÉE' : 'Macro Tendance désarmée', {
        direction: lastState?.trend?.direction, disponible: lastState?.trend?.available,
      });
      return;
    }

    case 'com.trader.ninjatrader.cooldown': {
      const resp = await bridge.sendCommand(createCommand('toggleCooldown', {}));
      if (resp.error) throw new Error(`${resp.error.code}: ${resp.error.message}`);
      return;
    }

    // Arme la macro, ou tente de la désarmer une fois le verrou expiré. Un refus pendant le
    // verrou est le comportement attendu, par conception.
    case 'com.trader.ninjatrader.safety': {
      // Aucun paramètre de contournement n'est transmis, et le bridge n'en accepte plus : le
      // verrou ne se lève qu'à son échéance. Un appui pendant le verrou est donc refusé, ce qui
      // est le service rendu et non une panne.
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

// --- Auto BE : pose le break-even dès que le gain atteint le seuil ---
//
// Seul automatisme du système à émettre un ordre sans appui de touche. Trois précautions en
// découlent : il ne part qu'une fois par prix moyen, il abandonne après quelques échecs plutôt
// que de marteler, et son armement est visible en permanence sur la touche.
//
// L'armement SURVIT au redémarrage (persisté dans `autobe.json`, voir `persisterArmementAutoBe`) :
// une protection que le trader croit armée doit l'être encore après un plantage de l'hôte. En
// revanche la mémoire de la pose en cours, elle, ne survit pas — elle est propre à une position
// et n'aurait aucun sens au redémarrage.
const autoBe = {
  /**
   * Armement. Persisté dans l'état du poste — jamais dans le layout, que Bitlearn réécrit
   * intégralement à chaque poussée.
   */
  actif: false,
  /** Prix moyen pour lequel le break-even a déjà été posé. */
  pose: null as number | null,
  envoiEnCours: false,
  echecs: 0,
  dernierEssai: 0,
  dernierAvert: 0,
  dernierAvertStop: 0,
  /** Limite la fréquence de l'avertissement de réglage impossible — l'évaluation tourne à 5 Hz. */
  dernierAvertConfig: 0,
};

const AUTOBE_MAX_ECHECS = 5;
const AUTOBE_DELAI_RETENTE_MS = 2000;

/**
 * Première touche portant cette action, quelle que soit la page.
 *
 * Les réglages qui appartiennent au bridge (temporisation, limites de sécurité) et les
 * automatismes vivent indépendamment de la page affichée : les chercher partout évite qu'un
 * réglage cesse de s'appliquer parce que sa touche est sur une autre page.
 */
function trouverTouche(actionId: string): SlotAssignment | null {
  for (const p of store.layout.pages) {
    for (const a of Object.values(p.slots)) {
      if (a.actionId === actionId) return a;
    }
  }
  return null;
}

function trouverAutoBe(): SlotAssignment | null {
  return trouverTouche('host.autobe');
}

/**
 * L'armement vit dans l'état du POSTE, à côté de celui de la macro de sécurité — surtout pas dans
 * le layout.
 *
 * Il y a été, et c'était juste tant que le layout appartenait à l'hôte. Depuis que Bitlearn en est
 * la source de vérité, le layout local n'est plus qu'un **cache à sens unique** : chaque poussée
 * le remplace intégralement, et l'armement disparaissait avec. Il suffisait d'éditer n'importe
 * quelle touche dans l'éditeur — ou de redémarrer l'hôte, dont la première synchronisation
 * réapplique la disposition de Bitlearn — pour désarmer l'Auto BE sans que rien ne le signale.
 *
 * Un armement est un état d'exécution, pas une configuration : sa place est ici, avec le verrou de
 * la macro et le compteur de séance, dans un fichier que Bitlearn ne réécrit jamais.
 */
const AUTOBE_STATE_PATH = join(DEFAULT_DATA_DIR, 'autobe.json');

function persisterArmementAutoBe(): void {
  try {
    mkdirSync(dirname(AUTOBE_STATE_PATH), { recursive: true });
    writeFileSync(AUTOBE_STATE_PATH, JSON.stringify({ armed: autoBe.actif }, null, 2), 'utf8');
  } catch (err) {
    // Ne jamais faire échouer un appui de touche pour un défaut d'écriture : l'armement reste
    // valable pour cette session, il ne survivra simplement pas au redémarrage.
    log.fail('AutoBE', err, 'Armement non persisté — il sera perdu au prochain démarrage');
  }
}

/** Reprend l'armement enregistré au démarrage. */
function restaurerArmementAutoBe(): void {
  let arme = false;
  try {
    if (existsSync(AUTOBE_STATE_PATH)) {
      arme = JSON.parse(readFileSync(AUTOBE_STATE_PATH, 'utf8'))?.armed === true;
    }
  } catch (err) {
    log.fail('AutoBE', err, 'État d\'armement illisible — automatisme considéré désarmé');
  }

  // Reprise des installations antérieures, où l'armement vivait dans le layout. Lu une seule fois :
  // la prochaine poussée de Bitlearn effacera cette clé, et c'est bien pour cela qu'on en part.
  const cfg = trouverAutoBe();
  if (!arme && cfg?.settings?.armed === true) {
    arme = true;
    log.event('AutoBE', 'Armement récupéré depuis l\'ancien emplacement (layout) et déplacé');
  }

  if (!arme) return;

  autoBe.actif = true;
  persisterArmementAutoBe();
  log.event('AutoBE', 'Automatisme repris ARMÉ au démarrage', {
    seuilTicks: Number(cfg?.settings?.triggerTicks) || 0,
    offsetTicks: Number(cfg?.settings?.offsetTicks) || 0,
  });
}

function evaluerAutoBe(state: TradingState): void {
  if (!autoBe.actif || autoBe.envoiEnCours) return;

  const cfg = trouverAutoBe();
  if (!cfg) return;

  const pos = state.position;
  if (!pos?.exists) {
    // Position fermée : on réarme pour la suivante.
    if (autoBe.pose !== null || autoBe.echecs) {
      log.event('AutoBE', 'Position fermée — automatisme réarmé');
      autoBe.pose = null;
      autoBe.echecs = 0;
    }
    return;
  }

  const info = state.instrumentInfo;
  if (!info || info.tickSize <= 0 || info.lastPrice <= 0) {
    // Sans taille de tick ni prix, le seuil est incalculable. Averti au plus une fois par minute :
    // l'évaluation tourne cinq fois par seconde.
    if (Date.now() - autoBe.dernierAvert > 60_000) {
      autoBe.dernierAvert = Date.now();
      log.eventWarn('AutoBE', 'Prix ou taille de tick indisponibles — seuil non évaluable', {
        tickSize: info?.tickSize ?? 0, lastPrice: info?.lastPrice ?? 0,
      });
    }
    return;
  }

  // Aucune condition sur l'existence d'un stop : l'add-on en CRÉE un s'il n'y en a pas.
  // Une stratégie ATM n'est donc plus nécessaire pour être protégé.
  const declenchement = Number(cfg.settings?.triggerTicks) || 0;
  if (declenchement <= 0) return;

  // Un décalage supérieur ou égal au seuil place le break-even AU-DELÀ du marché à l'instant même
  // où il se déclenche : NinjaTrader le refuse en `INVALID_STOP_PRICE`, l'automatisme retente cinq
  // fois puis abandonne pour toute la durée de la position. Le trader croit sa protection posée
  // alors qu'elle n'existe pas.
  //
  // Vécu le 10/08/2026 avec seuil=8 et décalage=60 : dix rejets en quatre minutes, deux positions
  // laissées sans protection. Refuser d'essayer et le DIRE vaut mieux qu'un échec silencieux.
  const decalage = Number(cfg.settings?.offsetTicks) || 0;
  if (decalage >= declenchement) {
    if (Date.now() - autoBe.dernierAvertConfig > 60_000) {
      autoBe.dernierAvertConfig = Date.now();
      log.eventWarn('AutoBE', 'Réglage impossible — le break-even serait toujours du mauvais côté du marché', {
        seuilTicks: declenchement, decalageTicks: decalage,
        correction: `le décalage doit rester sous le seuil (${declenchement})`,
      });
    }
    return;
  }

  // Un renfort déplace le prix moyen : la comparaison se fait à un demi-tick près, car ce sont
  // des flottants et l'égalité stricte finirait par reposer un break-even déjà posé.
  const dejaPose = autoBe.pose !== null && Math.abs(autoBe.pose - pos.averagePrice) < info.tickSize / 2;
  if (dejaPose) return;

  // Le prix moyen a bougé alors qu'un break-even était déjà posé : c'est un renfort. Le tracer
  // ici rend le réarmement lisible dans le journal, entre la modification de position et la
  // nouvelle pose.
  if (autoBe.pose !== null) {
    log.event('AutoBE', 'Prix moyen modifié — automatisme réarmé pour le nouveau seuil', {
      ancienPrixMoyen: autoBe.pose, nouveauPrixMoyen: pos.averagePrice, quantite: pos.quantity,
    });
    autoBe.pose = null;
    autoBe.echecs = 0;
  }

  const gain = gainEnTicks(state);
  if (gain === null || gain < declenchement) return;

  if (autoBe.echecs >= AUTOBE_MAX_ECHECS) return;
  if (Date.now() - autoBe.dernierEssai < AUTOBE_DELAI_RETENTE_MS) return;

  const offset = Number(cfg.settings?.offsetTicks) || 0;
  const prixMoyen = pos.averagePrice;
  autoBe.envoiEnCours = true;
  autoBe.dernierEssai = Date.now();

  log.event('AutoBE', 'Seuil atteint — pose du break-even', {
    gainTicks: gain.toFixed(1), seuil: declenchement, offsetTicks: offset,
    prixMoyen, direction: pos.direction, quantite: pos.quantity,
  });

  // L'add-on recalcule lui-même le prix depuis le prix moyen courant : sur un renfort, il suffit
  // de renvoyer la commande pour que le break-even suive.
  void sendCmd('breakeven', { offsetTicks: offset }, cfg.settings ?? {})
    .then(() => {
      autoBe.pose = prixMoyen;
      autoBe.echecs = 0;
      log.event('AutoBE', 'Break-even posé', { prixMoyen, offsetTicks: offset });
    })
    .catch((err) => {
      autoBe.echecs++;
      log.fail('AutoBE', err, 'Pose du break-even refusée', {
        essai: autoBe.echecs, sur: AUTOBE_MAX_ECHECS, prixMoyen,
      });
      if (autoBe.echecs >= AUTOBE_MAX_ECHECS) {
        log.eventWarn('AutoBE', 'Abandon après échecs répétés — reprendre la main manuellement', { prixMoyen });
      }
    })
    .finally(() => {
      autoBe.envoiEnCours = false;
      void paintAll();
      server.broadcastSnapshot();
    });
}

// --- Auto TP/SL : pose le take profit et le stop loss dès qu'une position s'ouvre ---
//
// Second automatisme capable d'émettre un ordre sans appui de touche, et il reprend les trois
// précautions de l'Auto BE : une seule pose par prix moyen, abandon après quelques échecs plutôt
// qu'un martèlement, armement visible en permanence sur la touche.
//
// La différence avec l'Auto BE tient au moment : celui-ci n'attend aucun gain, il protège dès que
// la position existe. « Dès qu'elle existe » et non « avec l'ordre d'entrée » : `Account.Submit`
// rend la main avant l'exécution, et un prix de déclenchement lu à cet instant serait une
// supposition. Le prix moyen publié par NinjaTrader est le seul qui soit vrai — et le suivre est
// aussi ce qui fait recalculer les deux jambes à chaque renfort, sans cas particulier à écrire.
const autoTpSl = {
  /** Armement. Persisté dans l'état du poste, jamais dans le layout — voir `persisterArmementAutoBe`. */
  actif: false,
  /** Prix moyen pour lequel le bracket a déjà été posé. */
  pose: null as number | null,
  /**
   * Distances avec lesquelles cette pose a été faite.
   *
   * Mémorisées en plus du prix moyen pour que MODIFIER un réglage en séance prenne effet sur la
   * position en cours. Sans elles, le trader corrigeait son stop dans l'éditeur, voyait la touche
   * annoncer « POSE », et ne découvrait qu'au trade suivant que l'ancienne valeur s'appliquait
   * toujours.
   */
  poseTp: 0,
  poseSl: 0,
  envoiEnCours: false,
  echecs: 0,
  dernierEssai: 0,
  /** Limite la fréquence de l'avertissement de tick manquant — l'évaluation tourne à 5 Hz. */
  dernierAvert: 0,
};

/** Oublie la pose en cours : la prochaine évaluation reposera le bracket. */
function reinitialiserAutoTpSl(): void {
  autoTpSl.pose = null;
  autoTpSl.poseTp = 0;
  autoTpSl.poseSl = 0;
  autoTpSl.echecs = 0;
}

const AUTOTPSL_MAX_ECHECS = 5;
const AUTOTPSL_DELAI_RETENTE_MS = 2000;

const AUTOTPSL_STATE_PATH = join(DEFAULT_DATA_DIR, 'autotpsl.json');

/**
 * Les deux distances, en ticks, telles que l'add-on les attend.
 *
 * Arrondies ici et pas seulement dans l'éditeur : le bridge refuse une décimale en `INVALID_PAYLOAD`
 * — à raison, puisque l'add-on la lirait comme absente, c'est-à-dire comme « pas de protection ».
 * Poser 20 quand 20,4 a été saisi vaut mieux que ne rien poser du tout.
 *
 * Toute valeur négative ou illisible vaut 0, c'est-à-dire « jambe non posée » : c'est le sens que
 * la touche affiche et celui que l'add-on applique.
 */
function distancesTpSl(settings: Record<string, unknown>): { tp: number; sl: number } {
  const lire = (valeur: unknown): number => {
    const n = Number(valeur);
    return Number.isFinite(n) && n > 0 ? Math.round(n) : 0;
  };
  return { tp: lire(settings.takeProfitTicks), sl: lire(settings.stopLossTicks) };
}

function persisterArmementAutoTpSl(): void {
  try {
    mkdirSync(dirname(AUTOTPSL_STATE_PATH), { recursive: true });
    writeFileSync(AUTOTPSL_STATE_PATH, JSON.stringify({ armed: autoTpSl.actif }, null, 2), 'utf8');
  } catch (err) {
    // Comme pour l'Auto BE : jamais faire échouer un appui pour un défaut d'écriture. L'armement
    // reste valable pour cette session, il ne survivra simplement pas au redémarrage.
    log.fail('AutoTPSL', err, 'Armement non persisté — il sera perdu au prochain démarrage');
  }
}

function restaurerArmementAutoTpSl(): void {
  let arme = false;
  try {
    if (existsSync(AUTOTPSL_STATE_PATH)) {
      arme = JSON.parse(readFileSync(AUTOTPSL_STATE_PATH, 'utf8'))?.armed === true;
    }
  } catch (err) {
    log.fail('AutoTPSL', err, 'État d\'armement illisible — automatisme considéré désarmé');
  }

  if (!arme) return;

  autoTpSl.actif = true;
  const cfg = trouverTouche('host.autotpsl');
  const { tp, sl } = distancesTpSl(cfg?.settings ?? {});
  log.event('AutoTPSL', 'Automatisme repris ARMÉ au démarrage', { takeProfitTicks: tp, stopLossTicks: sl });

  // Une macro reprise armée alors que ses distances sont revenues à 0 ne posera rien. La touche le
  // dit (« REGLER »), mais le journal doit le dire aussi : c'est le genre d'écart qu'on ne
  // découvre autrement qu'en constatant l'absence de stop sur une position déjà ouverte.
  if (tp === 0 && sl === 0) {
    log.eventWarn('AutoTPSL', 'Macro armée mais aucune distance réglée — rien ne sera posé', {
      correction: 'renseigner un Take Profit et/ou un Stop Loss dans les réglages de la touche',
    });
  }
}

function evaluerAutoTpSl(state: TradingState): void {
  if (!autoTpSl.actif || autoTpSl.envoiEnCours) return;

  const cfg = trouverTouche('host.autotpsl');
  if (!cfg) return;

  const pos = state.position;
  if (!pos?.exists) {
    // Position fermée : on réarme pour la suivante. Rien à annuler ici — les deux jambes partent
    // liées en OCO, celle qui reste est annulée par NinjaTrader quand l'autre s'exécute.
    if (autoTpSl.pose !== null || autoTpSl.echecs) {
      log.event('AutoTPSL', 'Position fermée — automatisme réarmé');
      reinitialiserAutoTpSl();
    }
    return;
  }

  const { tp, sl } = distancesTpSl(cfg.settings ?? {});
  // Les deux jambes désactivées : il n'y a rien à envoyer. La touche affiche « REGLER » ; inutile
  // d'en journaliser davantage, l'évaluation tourne cinq fois par seconde.
  if (tp === 0 && sl === 0) return;

  const info = state.instrumentInfo;
  if (!info || info.tickSize <= 0) {
    // Sans taille de tick, aucune distance n'est convertible en prix. Le PRIX du marché, lui, n'est
    // pas exigé : l'add-on s'en sert seulement pour vérifier de quel côté tombe chaque jambe, et
    // ne pas connaître le marché ne doit jamais être une raison de laisser une position nue.
    if (Date.now() - autoTpSl.dernierAvert > 60_000) {
      autoTpSl.dernierAvert = Date.now();
      log.eventWarn('AutoTPSL', 'Taille de tick indisponible — protections non calculables', {
        instrument: state.instrument, tickSize: info?.tickSize ?? 0,
      });
    }
    return;
  }

  // Rien à refaire tant que le prix moyen ET les distances sont ceux de la dernière pose. Le prix
  // moyen se compare à un demi-tick près : ce sont des flottants, et l'égalité stricte finirait
  // par reposer en boucle un bracket déjà posé.
  if (autoTpSl.pose !== null) {
    const memePrix = Math.abs(autoTpSl.pose - pos.averagePrice) < info.tickSize / 2;
    const memesDistances = autoTpSl.poseTp === tp && autoTpSl.poseSl === sl;
    if (memePrix && memesDistances) return;

    log.event('AutoTPSL', memePrix
      ? 'Distances modifiées — protections repositionnées sur la position en cours'
      : 'Prix moyen modifié — protections recalculées pour le renfort', {
      ancienPrixMoyen: autoTpSl.pose, nouveauPrixMoyen: pos.averagePrice, quantite: pos.quantity,
      takeProfitTicks: tp, stopLossTicks: sl,
    });
    reinitialiserAutoTpSl();
  }

  if (autoTpSl.echecs >= AUTOTPSL_MAX_ECHECS) return;
  if (Date.now() - autoTpSl.dernierEssai < AUTOTPSL_DELAI_RETENTE_MS) return;

  const prixMoyen = pos.averagePrice;
  autoTpSl.envoiEnCours = true;
  autoTpSl.dernierEssai = Date.now();

  log.event('AutoTPSL', 'Position ouverte — pose du take profit et du stop loss', {
    takeProfitTicks: tp, stopLossTicks: sl,
    prixMoyen, direction: pos.direction, quantite: pos.quantity,
  });

  // L'add-on recalcule les deux prix depuis le prix moyen courant et adopte le sens de la position :
  // sur un renfort, il suffit de renvoyer la même commande pour que les protections suivent.
  void sendCmd('attachBracket', { takeProfitTicks: tp, stopLossTicks: sl }, cfg.settings ?? {})
    .then(() => {
      autoTpSl.pose = prixMoyen;
      autoTpSl.poseTp = tp;
      autoTpSl.poseSl = sl;
      autoTpSl.echecs = 0;
      log.event('AutoTPSL', 'Protections posées', { prixMoyen, takeProfitTicks: tp, stopLossTicks: sl });
    })
    .catch((err) => {
      autoTpSl.echecs++;
      log.fail('AutoTPSL', err, 'Pose des protections refusée', {
        essai: autoTpSl.echecs, sur: AUTOTPSL_MAX_ECHECS, prixMoyen,
      });
      if (autoTpSl.echecs >= AUTOTPSL_MAX_ECHECS) {
        // Le pire état possible pour cette macro : une position ouverte que la touche annonce
        // protégée et qui ne l'est pas. Il doit rester une ligne explicite dans le journal du jour.
        log.eventWarn('AutoTPSL', 'Abandon après échecs répétés — POSITION SANS PROTECTION AUTOMATIQUE', {
          prixMoyen, direction: pos.direction, quantite: pos.quantity,
        });
      }
    })
    .finally(() => {
      autoTpSl.envoiEnCours = false;
      void paintAll();
      server.broadcastSnapshot();
    });
}

/**
 * Pousse la durée de temporisation vers le bridge, qui la possède et l'applique.
 *
 * Rejouée à chaque connexion pour la même raison que les limites de sécurité : le bridge repart
 * sur sa valeur par défaut, et une durée réglée par le trader ne doit pas redevenir 60 s au
 * premier redémarrage.
 */
async function pushCooldownConfig(): Promise<void> {
  const cfg = trouverTouche('com.trader.ninjatrader.cooldown');
  const valeur = cfg?.settings?.cooldownSeconds;
  if (typeof valeur !== 'number' || !Number.isFinite(valeur) || !bridge.isConnected) return;

  const cooldownSeconds = Math.round(valeur);
  const resp = await bridge.sendCommand(createCommand('configureCooldown', { cooldownSeconds }));
  if (resp.error) {
    log.eventWarn('Cooldown', 'configureCooldown refusé par le bridge', {
      code: resp.error.code, reason: resp.error.message, requested: cooldownSeconds,
    });
  } else {
    log.event('Cooldown', 'Durée de temporisation poussée vers le bridge', { cooldownSeconds });
  }
}

/** Rejoue vers le bridge tous les réglages qu'il possède mais que le layout décrit. */
async function syncConfig(): Promise<void> {
  await syncSafetyConfig();
  await syncPauseConfig();
  await pushCooldownConfig();
  await syncTrendConfig();
  await syncCopierConfig();
}

/**
 * Pousse la configuration de la copie de comptes, portée par la touche Compte.
 *
 * Le compte MAÎTRE n'est pas transmis : le bridge le connaît déjà, c'est le compte sélectionné.
 * Un second endroit où le déclarer aurait été un second endroit où il peut diverger.
 *
 * Sans touche Compte dans le layout, rien n'est transmis et le bridge garde ce qu'il avait —
 * même posture que la pause : retirer une touche ne doit pas effacer en silence un réglage.
 * En revanche la touche PRÉSENTE avec la copie éteinte transmet bien `enabled: false`, et c'est
 * ce qui libère une copie retenue après un changement de compte maître.
 */
async function syncCopierConfig(): Promise<void> {
  const cfg = trouverTouche('com.trader.ninjatrader.account');
  if (!cfg || !bridge.isConnected) return;

  const enabled = cfg.settings?.copyEnabled === true;
  const groupe = parseFollowers(cfg.settings?.followers);
  const maitre = (lastState?.account ?? '').toUpperCase();

  // Sans compte connu, la soustraction est impossible : le groupe partirait entier, maître compris,
  // et le bridge refuserait TOUTE la configuration en `COPIER_MASTER_IS_FOLLOWER` — pour ensuite
  // continuer sur celle qu'il avait persistée, donc éventuellement sur une liste périmée.
  //
  // On ne pousse donc rien et on attend le premier état. `onStateUpdate` rappelle cette fonction
  // dès que le compte est connu, et le cas ne dure que quelques centaines de millisecondes.
  if (!maitre && groupe.length > 0) {
    log.event('Copier', 'Configuration non poussée — compte sélectionné encore inconnu', {
      groupe: groupe.length, correction: 'renvoi automatique dès la première publication d\'état',
    });
    return;
  }

  // Le réglage décrit le GROUPE de copie, maître compris. Les suiveurs effectifs s'en déduisent
  // à l'exécution : groupe moins le compte sélectionné.
  //
  // C'est ce qui fait basculer les rôles tout seul. Groupe {A, B, C}, maître A → on copie vers
  // B et C. La touche passe à B → on copie vers A et C, sans qu'une ligne du layout ait bougé.
  //
  // Et il FALLAIT que rien ne bouge : `PUT /api/tradedeck/layout` exige une session utilisateur,
  // le poste ne peut pas réécrire le layout côté Bitlearn. Une bascule qui aurait modifié la
  // liste localement aurait divergé du site en silence, jusqu'à la prochaine édition qui l'aurait
  // écrasée sans prévenir.
  const suiveurs = groupe.filter((f) => f.name.toUpperCase() !== maitre);

  // Chaque compte lié reçoit EXACTEMENT la quantité du maître : multiplicateur 1, aucun plafond.
  //
  // Normalisé ici, et pas seulement à l'écriture dans l'éditeur. Le moteur sait toujours
  // dimensionner par compte — c'est du code utile, gardé pour le jour où le réglage reviendra —
  // mais il n'existe plus aucun contrôle pour le régler. Une valeur héritée d'un layout ancien
  // continuerait donc de doubler ou de plafonner une taille sans que rien à l'écran ne le dise :
  // c'est le réglage invisible qui agit, le pire mode de défaillance de ce projet.
  const deviants = suiveurs.filter((f) => f.multiplier !== 1 || f.maxContracts !== 0);
  if (deviants.length > 0) {
    log.eventWarn('Copier', 'Dimensionnement hérité ignoré — chaque compte lié suit la quantité du maître', {
      comptes: deviants.map((f) => `${f.name}×${f.multiplier}/${f.maxContracts}`).join(' '),
    });
  }

  const followers = formatFollowers(
    suiveurs.map((f) => ({ name: f.name, multiplier: 1, maxContracts: 0 })),
  );

  const resp = await bridge.sendCommand(createCommand('configureCopier', { enabled, followers }));
  if (resp.error) {
    // Un refus ici n'est pas anodin : il veut dire que la copie que le trader croit configurée ne
    // tourne PAS. Compte réel interdit en mode sûr, liste trop longue.
    log.eventWarn('Copier', 'configureCopier refusé par le bridge', {
      code: resp.error.code, reason: resp.error.message, enabled, followers,
    });
    return;
  }

  const result = resp.result as { enabled?: boolean; followers?: number } | undefined;
  log.event('Copier', 'Configuration de copie poussée vers le bridge', {
    demande: enabled, effectif: result?.enabled,
    maitre: lastState?.account ?? '', groupe: groupe.length, suiveurs: result?.followers,
  });
}

/**
 * Pousse les réglages de la macro Tendance jusqu'à l'add-on, seul à pouvoir en faire quelque chose :
 * c'est lui qui détient les barres.
 *
 * Les unités de temps sont des réglages STRUCTURELS : les modifier fait recharger les séries, donc
 * repartir d'un état « NO DATA » de quelques secondes. L'add-on ne recharge que si la valeur a
 * réellement changé, ce qui compte ici : cette fonction est rejouée à chaque édition du layout ET à
 * chaque reconnexion.
 *
 * Un réglage masqué par `showIf` doit être NEUTRALISÉ et pas seulement caché — d'où le
 * `higherMinutes` omis quand la confirmation est coupée. Une règle invisible restée active est le
 * pire des deux mondes.
 */
async function syncTrendConfig(): Promise<void> {
  const cfg = trouverTouche('com.trader.ninjatrader.trend');
  if (!cfg || !bridge.isConnected) return;

  const s = cfg.settings ?? {};
  const higherEnabled = s.higherEnabled !== false;
  // Explicitement transmis à chaque poussée, jamais omis : c'est ce qui garantit que DÉCOCHER
  // l'autorisation atteigne le bridge et y désarme la macro. Un champ absent voudrait dire
  // « laisser tel quel », donc une protection décochée à l'écran mais toujours armée en séance.
  const payload: Record<string, unknown> = { higherEnabled, blockingAllowed: s.blocageAutorise === true };

  if (typeof s.referenceMinutes === 'number' && Number.isFinite(s.referenceMinutes)) {
    payload.referenceMinutes = Math.round(s.referenceMinutes);
  }
  if (higherEnabled && typeof s.higherMinutes === 'number' && Number.isFinite(s.higherMinutes)) {
    payload.higherMinutes = Math.round(s.higherMinutes);
  }
  if (typeof s.thresholdAtr === 'number' && Number.isFinite(s.thresholdAtr) && s.thresholdAtr > 0) {
    payload.thresholdAtr = s.thresholdAtr;
  }

  // Le bridge refuse une unité supérieure qui ne dépasse pas la référence : elle ne confirmerait
  // rien et produirait un FLAT permanent que rien n'expliquerait. Le dire ICI plutôt que de laisser
  // partir un INVALID_PAYLOAD, dont le message n'atteint pas la page de configuration.
  const ref = payload.referenceMinutes as number | undefined;
  const haut = payload.higherMinutes as number | undefined;
  if (ref !== undefined && haut !== undefined && haut <= ref) {
    log.eventWarn('Trend', 'Réglages ignorés : l\'unité supérieure doit dépasser l\'unité de référence', {
      referenceMinutes: ref, higherMinutes: haut,
    });
    return;
  }

  const resp = await bridge.sendCommand(createCommand('configureTrend', payload));
  if (resp.error) {
    log.eventWarn('Trend', 'configureTrend refusé par le bridge', {
      code: resp.error.code, raison: resp.error.message, requested: payload,
    });
  } else {
    log.event('Trend', 'Réglages de tendance poussés vers le bridge', payload);
  }
}

/**
 * Pousse les réglages de la pause obligatoire, qui a sa propre touche.
 *
 * Envoi séparé et non fondu dans `pushSafetyConfig` : le bridge n'accepte un changement pendant que
 * Guard est armé que si la charge ne contient QUE des champs de pause. Les mêler rendrait la pause
 * inconfigurable dès qu'on arme Guard — or elle n'est plus une règle de Guard.
 *
 * Sans touche Pause dans le layout, rien n'est transmis et le bridge garde ce qu'il avait. C'est
 * volontaire : retirer la touche ne doit pas effacer en silence une règle que le trader s'était
 * imposée.
 */
async function syncPauseConfig(): Promise<void> {
  for (const p of store.layout.pages) {
    for (const a of Object.values(p.slots)) {
      if (a.actionId !== 'com.trader.ninjatrader.pause') continue;

      const payload: Record<string, unknown> = {};
      for (const key of ['pauseAfterMinutes', 'pauseDurationMinutes']) {
        const value = a.settings?.[key];
        if (typeof value === 'number' && Number.isFinite(value)) payload[key] = value;
      }
      if (Object.keys(payload).length === 0 || !bridge.isConnected) return;

      const resp = await bridge.sendCommand(createCommand('configureSafety', payload));
      if (resp.error) {
        // `PAUSE_IN_PROGRESS` est un refus attendu : on ne change pas la règle pendant qu'elle
        // s'applique. Le dire sans en faire une erreur.
        const attendu = resp.error.code === 'PAUSE_IN_PROGRESS';
        const message = 'Réglages de pause non appliqués';
        if (attendu) log.event('Pause', message, { raison: resp.error.message });
        // `SAFETY_MACRO_LOCKED` sur une charge qui ne contient que des champs de pause ne peut
        // vouloir dire qu'une chose : le réglage saisi RELÂCHE la pause alors que Guard est armé.
        // C'est le contournement que le bridge refuse désormais, et il mérite d'être vu.
        else if (resp.error.code === 'SAFETY_MACRO_LOCKED') {
          log.eventWarn('Sécurité', 'Assouplissement de la pause REFUSÉ — Guard est armé', {
            raison: resp.error.message, demande: payload,
          });
        }
        else log.eventWarn('Pause', message, { code: resp.error.code, raison: resp.error.message });
      } else {
        log.event('Pause', 'Réglages de pause poussés vers le bridge', payload);
      }
      return;
    }
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

/** Déclenche réellement l'action d'un emplacement, et journalise l'issue. */
async function declencher(slot: number, assignment: SlotAssignment, maintenuMs = 0): Promise<void> {
  // Chaque appui est journalisé avant toute tentative : une touche qui n'a produit aucun ordre
  // et une touche dont l'ordre a été refusé se ressemblent sur le deck, et seule cette ligne
  // permet de les distinguer après coup.
  const startedAt = Date.now();
  log.event('KeyDown', `${actionName(assignment.actionId)} pressée`, {
    slot, actionId: assignment.actionId, maintenuMs: maintenuMs || undefined,
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
}

// --- Confirmation par appui long ---

/**
 * Au-delà de cette durée, un maintien est traité comme « long » : décompte en secondes plutôt que
 * jauge seule, et rafraîchissement ralenti. À 20 s, un tic de 80 ms ne fait progresser la jauge que
 * de 0,4 % — 250 écritures USB pour une barre qui paraît immobile.
 */
const MAINTIEN_LONG_MS = 5000;

/** Maintien en cours. `null` la plupart du temps : une seule touche à la fois. */
let maintien: { slot: number; assignment: SlotAssignment; debut: number; duree: number; timer: ReturnType<typeof setInterval> } | null = null;

/** Progression 0..1 de la touche en cours de maintien, lue par `visualFor`. */
function progressionDe(slot: number): number | undefined {
  if (!maintien || maintien.slot !== slot) return undefined;
  return Math.min(1, (Date.now() - maintien.debut) / maintien.duree);
}

/** Secondes restantes d'un maintien long. `undefined` sur un maintien court, où la jauge suffit. */
function restantDe(slot: number): number | undefined {
  if (!maintien || maintien.slot !== slot || maintien.duree <= MAINTIEN_LONG_MS) return undefined;
  return Math.max(0, Math.ceil((maintien.debut + maintien.duree - Date.now()) / 1000));
}

/**
 * Durée de maintien imposée par l'Anti-Tilt, en millisecondes. 0 quand il ne s'applique pas.
 *
 * C'est la SEULE exception à la règle « une action instantanée ne se confirme jamais », et elle est
 * volontairement étroite : uniquement des entrées, uniquement pendant que le bridge signale
 * `tiltActive`, et jamais une sortie de position. `tiltAppliesTo` est partagé avec le rendu pour
 * que la touche annonce exactement ce que l'appui fera.
 */
function frictionAntiTilt(actionId: string): number {
  if (!lastState || !tiltAppliesTo(actionId, lastState)) return 0;
  return Math.max(1, lastState.safety.tiltHoldSeconds) * 1000;
}

/**
 * Maintien exigé pour armer ou désarmer la macro Tendance, en millisecondes.
 *
 * 1,5 s : bien au-delà des 600 ms d'une confirmation, qui ne protègent que d'un frôlement, et très
 * en deçà des 20 s de l'Anti-Tilt, qui sont une punition volontaire. Armer une protection est un
 * geste délibéré, pas une épreuve — et la jauge se remplit assez lentement pour qu'on voie ce qu'on
 * est en train de faire.
 *
 * 0 quand le blocage n'est pas autorisé sur la touche : l'appui redevient instantané et ne fait que
 * redemander l'état. C'est ce qui rend la fonction réellement optionnelle — sans l'autorisation,
 * il n'existe aucun geste capable d'armer quoi que ce soit.
 */
const TREND_ARM_HOLD_MS = 1500;

function maintienArmementTendance(actionId: string): number {
  if (actionId !== 'com.trader.ninjatrader.trend') return 0;
  return lastState?.trend?.blockingAllowed ? TREND_ARM_HOLD_MS : 0;
}

function annulerMaintien(raison: string): void {
  if (!maintien) return;
  clearInterval(maintien.timer);
  const { slot, assignment, debut, duree } = maintien;
  maintien = null;
  const tenuMs = Date.now() - debut;
  log.event('KeyDown', `${actionName(assignment.actionId)} annulée — maintien trop court`, {
    slot, tenuMs, requisMs: duree, raison,
  });
  journal.recordHoldAbandoned(assignment.actionId, tenuMs, duree, {
    account: lastState?.account ?? '', instrument: lastState?.instrument ?? '',
  });
  void paintAll();
}

device.onPress(async (ev) => {
  const assignment = slotAt(ev.index);
  if (!assignment) {
    log.traceEvent('KeyDown', 'Appui sur un emplacement vide', { slot: ev.index });
    return;
  }

  // Une action qui entre ou sort d'une position part toujours immédiatement, quel que soit le
  // réglage enregistré. La règle est appliquée ici et non seulement masquée dans l'interface :
  // un layout écrit avant cette règle, ou édité à la main, ne doit pas pouvoir retarder une
  // sortie de position.
  const def = CATALOG_BY_ID.get(assignment.actionId);
  const demandee = assignment.settings?.holdConfirm === true;
  if (def?.instant && demandee) {
    log.eventWarn('KeyDown', 'Confirmation ignorée : cette action doit être instantanée', {
      slot: ev.index, actionId: assignment.actionId,
    });
  }
  const confirmation = def?.instant || !demandee ? 0 : HOLD_CONFIRM_MS;

  // L'Anti-Tilt passe outre la règle « instantané » ci-dessus, et lui seul. La friction ne refuse
  // rien : l'ordre part toujours, il faut seulement le vouloir pendant toute la durée du maintien.
  const friction = frictionAntiTilt(assignment.actionId);
  if (friction > 0) {
    log.eventWarn('KeyDown', 'Anti-Tilt : appui long exigé sur cette entrée', {
      slot: ev.index, actionId: assignment.actionId,
      motif: lastState?.safety?.tiltReason, requisMs: friction,
    });
  }
  // La Tendance passe outre la règle « instantané » comme l'Anti-Tilt, mais dans l'autre sens :
  // ici le maintien n'est pas une friction sur un ordre, c'est le geste d'armement lui-même. Un
  // appui trop court s'annule et ne déclenche rien — ce qui est exactement le comportement voulu,
  // la touche restant un indicateur qu'on peut regarder sans risque de l'armer par mégarde.
  const armement = maintienArmementTendance(assignment.actionId);

  const duree = Math.max(confirmation, friction, armement);

  if (duree <= 0) {
    await declencher(ev.index, assignment);
    return;
  }

  // Un second appui pendant un maintien abandonne le premier : sans cela, deux touches à
  // confirmation pressées coup sur coup laisseraient un minuteur orphelin qui finirait par
  // envoyer un ordre que le trader croyait abandonné.
  if (maintien) annulerMaintien('autre touche pressée');

  const debut = Date.now();
  // 12 Hz : assez fluide pour que la jauge paraisse continue, assez espacé pour ne réécrire
  // qu'une touche à chaque tic grâce au rendu différentiel. Sur un maintien long, 4 Hz suffit —
  // le décompte ne change qu'une fois par seconde, et cela divise par trois les écritures USB.
  const periode = duree > MAINTIEN_LONG_MS ? 250 : 80;
  const timer = setInterval(() => {
    if (!maintien) return;
    if (Date.now() - maintien.debut >= maintien.duree) {
      const { slot, assignment: a, debut: d } = maintien;
      clearInterval(maintien.timer);
      maintien = null;
      void declencher(slot, a, Date.now() - d);
      return;
    }
    void paintAll();
  }, periode);

  maintien = { slot: ev.index, assignment, debut, duree, timer };
  log.debugEvent('KeyDown', `${actionName(assignment.actionId)} — maintien demandé`, { slot: ev.index, requisMs: duree });
  await paintAll();
});

device.onRelease((ev) => {
  // Relâchement avant l'échéance : l'action est abandonnée. C'est tout l'intérêt du geste.
  if (maintien && maintien.slot === ev.index) annulerMaintien('relâchée');
});

/** Le boîtier a-t-il déjà disparu au moins une fois ? Distingue un retour d'un démarrage. */
let boitierDejaPerdu = false;

device.onConnectionChange((connected) => {
  log.event('Device', connected ? 'Boîtier disponible' : 'Boîtier indisponible');

  // Perdre le boîtier pendant que la macro est armée n'est pas un incident matériel comme un
  // autre : c'est la seule façon de faire taire la surface de contrôle sans toucher aux
  // sécurités. Le 12/08/2026 le boîtier a été débranché à 11 h 57 alors que la limite de perte
  // journalière était franchie depuis 10 h 31, et le trading a continué une heure dans
  // NinjaTrader. L'add-on appliquait toujours les règles — mais rien, nulle part, ne notait le
  // moment où le trader avait quitté la table.
  //
  // Le bridge tient le cas jumeau (cet hôte s'arrête) ; celui-ci tient le cas que le bridge ne
  // peut pas voir : le câble arraché alors que le processus vit toujours et que la socket reste
  // ouverte. Il faut les deux.
  const s = lastState?.safety;

  // La toute première connexion est le démarrage de l'hôte et non un retour : l'enregistrer
  // ajouterait une ligne à chaque lancement et noierait le décompte des vraies pertes.
  if (!connected) boitierDejaPerdu = true;
  if (boitierDejaPerdu) {
    if (!connected && s?.armed) {
      log.eventWarn('Sécurité', 'BOÎTIER PERDU alors que la macro est ARMÉE', {
        verrouSecondes: s.lockSecondsRemaining, entreesBloquees: s.entriesBlocked,
        motif: s.blockReason || '-', pnlSession: s.sessionPnl,
      });
    }
    const type = connected ? 'deck.reconnected' : (s?.armed ? 'deck.lostWhileArmed' : 'deck.disconnected');
    journal.record(type, {
      account: lastState?.account ?? '', instrument: lastState?.instrument ?? '',
    }, {
      armed: s?.armed === true,
      lockSecondsRemaining: s?.lockSecondsRemaining ?? 0,
      entriesBlocked: s?.entriesBlocked === true,
      blockReason: s?.blockReason ?? '',
      sessionPnl: s?.sessionPnl ?? 0,
    });
  }

  if (connected) {
    device.setBrightness(store.layout.brightness);
    void paintAll();
  }
  server.broadcastSnapshot();
});

// --- État venant du bridge ---

/** Dernière empreinte connue, pour ne journaliser que les changements. */
let empreinte: Empreinte | null = null;

/**
 * Compte sur lequel la configuration de copie a été calculée pour la dernière fois.
 *
 * La liste envoyée au bridge est le groupe MOINS le compte sélectionné : elle dépend donc du
 * compte, et doit être recalculée dès qu'il change — y compris quand ce n'est pas la touche qui
 * l'a changé. NinjaTrader en choisit un tout seul quand le compte suivi disparaît, et ce chemin
 * ne passe par aucun appui.
 */
let compteCopieurPousse: string | null = null;

bridge.onStateUpdate((state) => {
  lastState = state;

  // Le compte a changé : la liste des comptes liés n'est plus la bonne.
  //
  // C'est aussi ce qui rattrape la reconnexion du bridge. `syncConfig` y part immédiatement, alors
  // que `lastState` vient d'être remis à null : le maître était alors inconnu, donc non soustrait,
  // et le groupe partait tel quel — maître compris. Le bridge refusait toute la configuration en
  // `COPIER_MASTER_IS_FOLLOWER` et continuait sur celle qu'il avait persistée, sans que le deck
  // ne montre quoi que ce soit d'anormal.
  if (state.account && state.account !== compteCopieurPousse) {
    compteCopieurPousse = state.account;
    void syncCopierConfig();
  }

  // Avant tout traitement : c'est ce qui donne le contexte de tout ce qui suit dans le journal.
  const nouvelle = empreinteDe(state);
  journaliserTransitions(empreinte, nouvelle);
  // Même comparaison, second consommateur : le journal comportemental. Redétecter les
  // transitions de son côté aurait garanti que les deux finissent par diverger.
  journal.observe(empreinte, nouvelle);
  // Échantillon de solde scellé, limité en fréquence par le journal lui-même. Il ne remplace pas
  // le solde envoyé avec le lot — celui-là cale le capital de départ du journal — il alimente le
  // capital de RÉFÉRENCE de l'XP, pris en médiane, et sa réconciliation avec le P&L cumulé.
  if (typeof state.cashValue === 'number') journal.recordBalance(nouvelle.account, state.cashValue);
  // Retour à plat : l'aller-retour vient de se terminer, c'est le moment où il devient
  // consultable dans Bitlearn. Attendre le tour périodique ferait patienter une minute pour
  // un trade que l'on vient de clôturer et que l'on veut relire tout de suite.
  if (empreinte?.posExists && !nouvelle.posExists) void uploader.flush('retour à plat');
  empreinte = nouvelle;

  if (state.cooldownActive) startCooldownTimer();
  // Évalué à chaque état, soit cinq fois par seconde : c'est ce qui permet à l'automatisme de
  // suivre le prix et de réagir à un renfort de position sans attendre.
  evaluerAutoBe(state);
  // Même cadence, et elle compte davantage ici : entre l'exécution de l'entrée et la pose des
  // protections, la position est nue. Deux cents millisecondes sont ce qui sépare les deux.
  evaluerAutoTpSl(state);
  void paintAll();
  server.broadcastSnapshot();
});

bridge.onConnectionChange((connected) => {
  log.event('Connection', connected ? 'Bridge connecté' : 'Bridge déconnecté', { url: BRIDGE_URL });
  if (!connected) {
    lastState = null;
    // Sans cette remise à zéro, la configuration de copie ne repartirait jamais après une
    // reconnexion : `syncConfig` la saute faute de compte connu, et la publication d'état qui suit
    // trouve un compte INCHANGÉ, donc ne déclenche pas le renvoi. Le bridge resterait sur ce qu'il
    // avait persisté, une liste calculée pour un autre compte sélectionné.
    compteCopieurPousse = null;
  }
  // Le bridge redémarre sans mémoire de nos réglages : les repousser à chaque reconnexion.
  if (connected) void syncConfig();
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

  // Un ordre passé directement dans NinjaTrader pendant que la macro refuse les entrées.
  // L'add-on l'a annulé ; le deck doit le dire, sans quoi le seul témoin serait un fichier de log
  // que personne ne relit sur le moment.
  //
  // L'enregistrement au journal, lui, est passé côté bridge : c'est justement quand cet hôte-ci
  // n'est plus là que la violation compte le plus. Le 12/08/2026, le boîtier a été débranché et
  // ce processus s'est arrêté ; les dix-neuf ordres manuels qui ont suivi, tous au-delà de la
  // limite de perte journalière, n'ont atterri dans aucun journal. Un seul écrivain, celui qui
  // survit — sans quoi deux `eid` différents auraient compté chaque contournement deux fois.
  if (msg.action === 'guardViolation' && msg.payload) {
    const v = msg.payload as unknown as GuardViolation;
    lastViolationAt = Date.now();
    log.eventWarn('Guard', v.cancelled
      ? 'Ordre manuel ANNULÉ — la macro refusait les entrées'
      : 'Ordre manuel détecté mais NON annulé', {
      motif: v.violation, action: v.orderAction, type: v.orderType,
      quantite: v.quantity, instrument: v.instrument, erreur: v.error,
    });
    void paintAll();
    setTimeout(() => void paintAll(), VIOLATION_BANNER_MS + 200);
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
  // Écrite avant tout le reste : le fichier du jour devient autonome, on sait quelles macros
  // étaient posées sans avoir à retrouver le layout de l'époque.
  journaliserConfiguration(store.layout);
  restaurerArmementAutoBe();
  restaurerArmementAutoTpSl();

  // Constat, pas action : l'installateur dépose l'add-on, l'hôte se contente de dire s'il est
  // là. Sans cette ligne, un voyant NinjaTrader rouge n'a aucune trace exploitable — l'add-on
  // absent ne journalise rien, par définition. Pas attendu : la résolution du dossier Documents
  // passe par `reg.exe`, et rien ici n'a le droit de retarder le démarrage.
  localiserNinjaScript(() => journaliserEtat());

  // Avant toute connexion : libérer le boîtier ET la place plugin du bridge. Windows relance
  // l'application Elgato à l'ouverture de session même sans démarrage automatique, et son
  // plugin prend la seule place disponible — TradeDeck restait alors « hors ligne ».
  await neutraliserElgato();

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

  // Après `device.start()` : la grille du boîtier détermine quelle disposition demander, et elle
  // n'est connue qu'une fois l'USB ouvert.
  // Relu à chaque battement : le boîtier peut être branché après le démarrage et NinjaTrader se
  // connecter en cours de séance. C'est cet état changeant que l'éditeur affiche.
  const contexteSync = () => ({
    columns: device.connected ? device.columns : store.layout.device.columns,
    rows: device.connected ? device.rows : store.layout.device.rows,
    status: {
      deck: device.connected,
      deckModel: device.productName || '',
      bridge: bridge.isConnected,
      nt: lastState?.ntConnected ?? false,
      // Pourquoi NinjaTrader est hors ligne, quand il l'est. Le booléen seul recouvrait trois
      // causes que Bitlearn ne pouvait pas départager : plateforme absente, add-on jamais
      // déposé, add-on déposé mais pas encore compilé.
      ntAddon: etatAddOn(),
      appVersion: VERSION,
    },
    // L'état exact que reçoit `computeVisual` : l'éditeur ayant le même moteur de visuels, il
    // dessine ce que le boîtier dessine. Une macro armée depuis le Deck s'y voit armée, sans
    // qu'aucun champ n'ait à être ajouté ici macro par macro.
    //
    // `lastState` est null tant que le bridge n'a rien publié — on n'envoie alors rien plutôt
    // qu'un état par défaut, qui se lirait comme « tout est au repos ».
    etat: lastState
      ? {
        capturedAt: Date.now(),
        state: lastState,
        autoBe: { actif: autoBe.actif, pose: autoBe.pose !== null },
        autoTpSl: { actif: autoTpSl.actif, pose: autoTpSl.pose !== null },
      }
      : undefined,
  });

  if (bitlearn.paired) {
    // Volontairement pas attendu : Bitlearn injoignable ne doit retarder le démarrage d'aucune
    // milliseconde, le cache local suffit à trader.
    bitlearn.startLayoutSync(store, contexteSync);
    // Relu à chaque envoi plutôt que capturé : cocher « Journaliser ce compte » doit prendre
    // effet sans redémarrer l'hôte.
    uploader.start();
  } else {
    // L'appairage attend un clic humain, jusqu'à cinq minutes. Il ne peut donc pas se trouver
    // sur le chemin du démarrage : le deck est déjà opérationnel pendant ce temps, sur la
    // disposition en cache.
    log.event('Bitlearn', 'Poste non appairé — disposition locale utilisée en attendant');
    void bitlearn.requestPairing(hostname(), VERSION).then((ok) => {
      if (ok) {
        bitlearn.startLayoutSync(store, contexteSync);
        uploader.start();
      }
    });
  }

  log.event('Session', 'Hôte prêt', { ui: url, bridge: BRIDGE_URL, layout: store.path });
  process.stdout.write(`\n  Interface de configuration : ${url}\n\n`);
}

async function shutdown(signal: string): Promise<void> {
  log.event('Session', 'Arrêt demandé', { signal });
  // Avant de couper le lien : c'est le dernier rattrapage possible avant l'extinction.
  await uploader.flush('arrêt');
  uploader.stop();
  bitlearn.stop();
  supervisor.stop();
  // Le bridge n'est volontairement pas tué (il porte le verrou de sécurité), mais notre client
  // doit cesser de se reconnecter, sinon le minuteur maintient le processus en vie.
  bridge.stop();
  server.close();
  await device.close();
  process.exit(0);
}

process.on('SIGINT', () => void shutdown('SIGINT'));
process.on('SIGTERM', () => void shutdown('SIGTERM'));

void main();
