/**
 * Calcul du visuel d'une touche à partir de l'action, de ses réglages et de l'état de trading.
 *
 * Port fidèle de `computeVisual` (`src/streamdeck-ninjatrader/src/plugin.ts:261-500`). Les
 * commentaires expliquant un choix d'affichage sont conservés : ils décrivent des règles de
 * sécurité, pas du style.
 */
import { Colors, ButtonVisual } from './visuals.js';
import { TradingState, SafetyStatus, DEFAULT_SAFETY_STATUS } from './messages.js';
import { getDisplayText, StatusType } from './status-display.js';

/** Contexte que l'hôte fournit au moteur, en plus de l'état publié par le bridge. */
export interface VisualContext {
  bridgeConnected: boolean;
  /** Dernier rejet remonté par NinjaTrader, affiché brièvement sur les touches d'entrée. */
  lastRejectionAt: number | null;
  /** Dernier ordre manuel annulé par l'add-on pendant un blocage — affiché sur la touche Sécurité. */
  lastViolationAt: number | null;
  defaultQuantity: number;
  /** État de l'automatisme Auto BE — vit dans l'hôte, pas dans l'état publié par le bridge. */
  autoBe: { actif: boolean; pose: boolean };
}

/** Gain en ticks au-delà du prix moyen, dans le sens de la position. */
export function gainEnTicks(state: TradingState): number | null {
  const pos = state.position;
  const info = state.instrumentInfo;
  if (!pos?.exists || !info || info.tickSize <= 0 || info.lastPrice <= 0) return null;
  const ecart = pos.direction === 'Long'
    ? info.lastPrice - pos.averagePrice
    : pos.averagePrice - info.lastPrice;
  return ecart / info.tickSize;
}

const REJECTION_BANNER_MS = 5000;

/**
 * Bien plus long que la bannière de rejet : un ordre manuel annulé pendant un blocage n'est pas un
 * incident technique passager, c'est un contournement qui vient d'être arrêté. Il doit rester
 * visible assez longtemps pour être vu, pas seulement enregistré dans un fichier.
 */
export const VIOLATION_BANNER_MS = 20000;

function formatAccountLabel(account: string): string {
  const value = account.trim();
  if (!value) return 'ACCT';

  const compact = value.replace(/[^A-Za-z0-9]/g, '').toUpperCase();
  const letters = (value.match(/[A-Za-z]/g) ?? []).join('').toUpperCase();
  const digits = (value.match(/\d/g) ?? []).join('');

  const prefix = (letters.length >= 3 ? letters : compact || value.toUpperCase()).slice(0, 3);
  const suffix = digits.length >= 3
    ? digits.slice(-3)
    : compact.length > prefix.length
      ? compact.slice(-3)
      : '';

  return suffix ? `${prefix}-${suffix}` : prefix;
}

function formatLockRemaining(seconds: number): string {
  if (seconds <= 0) return '--';
  if (seconds >= 3600) {
    const hours = Math.floor(seconds / 3600);
    const minutes = Math.floor((seconds % 3600) / 60);
    return `${hours}h${String(minutes).padStart(2, '0')}`;
  }
  if (seconds >= 60) return `${Math.floor(seconds / 60)}m`;
  return `${seconds}s`;
}

/** Durée courte et lisible sur une touche : `45s`, `2m`, `1m30`, `1h`. */
function formatDuree(seconds: number): string {
  if (seconds < 60) return `${seconds}s`;
  if (seconds >= 3600) {
    const h = Math.floor(seconds / 3600);
    const reste = Math.floor((seconds % 3600) / 60);
    return reste === 0 ? `${h}h` : `${h}h${String(reste).padStart(2, '0')}`;
  }
  const m = Math.floor(seconds / 60);
  const s = seconds % 60;
  return s === 0 ? `${m}m` : `${m}m${String(s).padStart(2, '0')}`;
}

/**
 * Bas de la touche Sécurité quand la macro est armée et que rien n'est encore atteint :
 * ce qu'il RESTE, sous la forme `trades-montant`, décomptant vers `0-0`.
 *
 * Volontairement exprimé en restant et non en consommé. « 4/15 » oblige à faire la soustraction
 * pour répondre à la seule question qui compte en séance — combien puis-je encore prendre — et
 * la réponse doit être lisible d'un coup d'œil sur une touche de 144 pixels.
 *
 * `--` signale une règle désactivée (limite à 0), à ne pas confondre avec une règle épuisée.
 */
function formatSafetyRestant(safety: SafetyStatus): string {
  const trades = safety.maxTradesWhenLosing > 0
    ? String(Math.max(0, safety.maxTradesWhenLosing - safety.tradeCount))
    : '--';

  let perte: string;
  if (safety.dailyLossLimit <= 0) {
    perte = '--';
  } else if (!safety.pnlAvailable) {
    // Sans P&L publié par NinjaTrader la règle de perte n'a aucune donnée : le dire plutôt que
    // d'afficher la limite entière, qui laisserait croire à une marge intacte.
    perte = '?';
  } else {
    // Marge avant la limite : le P&L est signé, donc une perte de 120 sur une limite de 300
    // laisse 300 + (-120) = 180. En profit la marge dépasse la limite, ce qui est exact —
    // il faut d'abord rendre le gain avant d'entamer la limite.
    perte = String(Math.max(0, Math.round(safety.dailyLossLimit + safety.sessionPnl)));
  }

  return `${trades}-${perte}`;
}

/**
 * Motif de l'Anti-Tilt, en assez court pour la troisième ligne d'une touche de 144 pixels.
 * Sans lui, le trader voit sa touche ralentie sans savoir laquelle de ses règles l'a décidé.
 */
function motifTilt(reason: SafetyStatus['tiltReason']): string {
  switch (reason) {
    case 'sizeEscalation': return 'SIZE UP';
    case 'giveBack': return 'GIVEBACK';
    case 'consecutiveLosses': return 'STREAK';
    case 'averaging': return 'AVERAGE';
    default: return '';
  }
}

/** Bas de la touche Sécurité quand une limite est atteinte : trades consommés et P&L de session. */
function formatSafetyDetail(safety: SafetyStatus): string {
  const trades = safety.maxTradesWhenLosing > 0
    ? `${safety.tradeCount}/${safety.maxTradesWhenLosing}`
    : `${safety.tradeCount}T`;

  // `renderButtonSvg` échappe désormais le XML lui-même : plus besoin d'éviter '&' ici.
  if (!safety.pnlAvailable) return `${trades} PNL?`;

  const pnl = Math.round(safety.sessionPnl);
  return `${trades} ${pnl >= 0 ? '+' : ''}${pnl}`;
}

/**
 * Ce qu'une touche d'entrée doit afficher à la place de son libellé normal.
 * La macro de sécurité prime sur la temporisation, car c'est la règle que le trader ne peut pas
 * lever. `dimmed` distingue un blocage réel (la touche ne fait rien) d'un simple avis transitoire
 * (la touche fonctionne toujours) — une touche grisée ne doit jamais vouloir dire
 * « votre ordre a été rejeté ».
 */
type EntryStatus = { label: string; dimmed: boolean } | null;

/**
 * Les cinq actions qui ouvrent ou retournent une position. Miroir exact de
 * `SafetyMacro.EntryActions` côté bridge : ce que la macro de sécurité bloque, l'Anti-Tilt ralentit.
 * Les sorties (`flatten`, `cancelOrders`) en sont volontairement absentes.
 */
export const ACTIONS_ENTREE = new Set([
  'com.trader.ninjatrader.buymarket',
  'com.trader.ninjatrader.sellmarket',
  'com.trader.ninjatrader.buylimit',
  'com.trader.ninjatrader.selllimit',
  'com.trader.ninjatrader.reverse',
]);

/**
 * L'Anti-Tilt ralentit-il cette touche en ce moment ?
 *
 * Exporté et utilisé aussi bien par le rendu que par le gestionnaire d'appui : une touche qui
 * afficherait HOLD tout en partant au premier appui — ou l'inverse — serait pire que pas de
 * friction du tout.
 */
export function tiltAppliesTo(actionId: string, state: TradingState): boolean {
  const safety = state.safety;
  if (!safety?.tiltActive || !ACTIONS_ENTREE.has(actionId)) return false;

  // Le moyennage porte sur l'EXPOSITION : il ne ralentit que les ordres qui l'augmentent.
  if (safety.tiltScope === 'increaseOnly') return augmenteExposition(actionId, state);

  return true;
}

/**
 * Cet ordre ferait-il GROSSIR la position ?
 *
 * Partagé par les deux règles qui ne visent que la croissance — le plafond de contrats, qui
 * refuse, et le moyennage, qui ralentit. Aucune des deux ne doit jamais gêner l'ordre qui réduit :
 * enfermer le trader dans une position trop grosse est le seul résultat qu'elles ne peuvent pas
 * produire. `reverse` n'y échappe pas : retourner ne réduit rien, cela remet la même taille en face.
 */
function augmenteExposition(actionId: string, state: TradingState): boolean {
  const direction = state.position?.exists ? state.position.direction : 'Flat';
  if (direction === 'Long' && actionId.includes('sell')) return false;
  if (direction === 'Short' && actionId.includes('buy')) return false;
  return true;
}

function entryStatus(state: TradingState, ctx: VisualContext, actionId: string): EntryStatus {
  if (state.safety?.entriesBlocked) {
    return {
      label: state.safety.blockReason === 'dailyLoss' ? 'LOSS LIMIT' : 'MAX TRADES',
      dimmed: true,
    };
  }
  // Plafond de contrats atteint. Seules les touches qui feraient GROSSIR la position sont grisées :
  // griser celle qui permet d'en sortir laisserait croire qu'on est coincé dedans. La touche
  // Sécurité, elle, ne bronche pas — être à sa taille normale n'est pas un incident.
  if (state.safety?.atContractCap && augmenteExposition(actionId, state)) {
    return { label: 'MAX QTY', dimmed: true };
  }
  if (state.cooldownActive) return { label: 'BLOCKED', dimmed: true };
  // Anti-Tilt : la touche fonctionne toujours, elle exige seulement d'être tenue. Jamais grisée
  // donc — le gris est réservé à ce qui ne part pas — et affichée après les deux vrais blocages,
  // qui priment parce qu'aucun maintien ne les lèvera.
  if (tiltAppliesTo(actionId, state)) {
    return { label: `HOLD ${state.safety.tiltHoldSeconds}s`, dimmed: false };
  }
  // NinjaTrader a refusé le dernier ordre : on prévient, mais la touche reste utilisable.
  if (ctx.lastRejectionAt && Date.now() - ctx.lastRejectionAt < REJECTION_BANNER_MS) {
    return { label: 'REJECTED', dimmed: false };
  }
  return null;
}

export function computeVisual(
  actionId: string,
  settings: Record<string, unknown>,
  state: TradingState,
  ctx: VisualContext,
): ButtonVisual | null {
  const connected = ctx.bridgeConnected && state.ntConnected;

  const pos = state.position;
  const qty = state.quantity ?? ctx.defaultQuantity;
  const defQty = state.defaultQuantity ?? ctx.defaultQuantity;

  switch (actionId) {
    case 'com.trader.ninjatrader.buymarket': {
      const st = entryStatus(state, ctx, actionId);
      return {
        title: 'MKT', subtitle: st?.label ?? `Buy ×${qty}`,
        bgColor: st?.dimmed ? Colors.disabled : (connected ? Colors.buyGreen : Colors.buyGreenDim),
        textColor: st?.dimmed ? Colors.textDim : '#FFFFFF',
      };
    }
    case 'com.trader.ninjatrader.sellmarket': {
      const st = entryStatus(state, ctx, actionId);
      return {
        title: 'MKT', subtitle: st?.label ?? `Sell ×${qty}`,
        bgColor: st?.dimmed ? Colors.disabled : (connected ? Colors.sellRed : Colors.sellRedDim),
        textColor: st?.dimmed ? Colors.textDim : '#FFFFFF',
      };
    }
    case 'com.trader.ninjatrader.buylimit': {
      const st = entryStatus(state, ctx, actionId);
      return {
        title: 'LMT', subtitle: st?.label ?? `Buy ×${qty}`,
        bgColor: st?.dimmed ? Colors.disabled : (connected ? Colors.buyGreen : Colors.buyGreenDim),
        textColor: st?.dimmed ? Colors.textDim : '#FFFFFF',
      };
    }
    case 'com.trader.ninjatrader.selllimit': {
      const st = entryStatus(state, ctx, actionId);
      return {
        title: 'LMT', subtitle: st?.label ?? `Sell ×${qty}`,
        bgColor: st?.dimmed ? Colors.disabled : (connected ? Colors.sellRed : Colors.sellRedDim),
        textColor: st?.dimmed ? Colors.textDim : '#FFFFFF',
      };
    }
    // « FLAT » et non « Close » : la touche voisine (cancelOrders) affiche déjà CLOSE, et deux
    // libellés identiques sur des commandes différentes est un piège en séance.
    case 'com.trader.ninjatrader.flatten':
      return { title: 'FLAT', subtitle: `Qty ${pos?.quantity ?? 0}`, bgColor: '#FFFFFF', textColor: '#000000' };

    case 'com.trader.ninjatrader.cancelorders': {
      const posQty = Math.abs(pos?.quantity ?? 0);
      return {
        title: 'QTY_CANCEL', subtitle: posQty > 0 ? `${posQty}` : '0',
        bgColor: posQty > 0 ? Colors.sellRed : '#FFFFFF',
        textColor: posQty > 0 ? '#FFFFFF' : '#000000',
      };
    }
    case 'com.trader.ninjatrader.cancelworkingorders': {
      // Annule uniquement les ordres en attente — la position n'est pas touchée.
      const orders = pos?.activeOrderCount ?? 0;
      return {
        title: 'CANCEL', subtitle: orders > 0 ? `${orders} order${orders > 1 ? 's' : ''}` : 'none',
        bgColor: orders > 0 ? Colors.cancelYellow : Colors.cancelYellowDim,
        textColor: orders > 0 ? '#000000' : Colors.textDim,
      };
    }
    case 'com.trader.ninjatrader.reverse': {
      // Inverser ouvre une position dans l'autre sens : la macro de sécurité s'applique aussi.
      const st = entryStatus(state, ctx, actionId);
      return {
        title: 'Invert', subtitle: st?.label ?? `Qty ${pos?.quantity ?? 0}`,
        bgColor: st?.dimmed ? Colors.disabled : '#FFFFFF',
        textColor: st?.dimmed ? Colors.textDim : '#000000',
      };
    }
    case 'com.trader.ninjatrader.breakeven': {
      const offset = (settings.offsetTicks as number) ?? 0;
      return { title: 'BE', subtitle: `+${offset}`, bgColor: Colors.flattenOrange, textColor: Colors.textWhite };
    }

    case 'com.trader.ninjatrader.stopplus':
      return { title: 'QTY_STOP_UP', subtitle: '1', bgColor: '#FFFFFF', textColor: '#000000' };
    case 'com.trader.ninjatrader.stopminus':
      return { title: 'QTY_STOP_DN', subtitle: '1', bgColor: '#FFFFFF', textColor: '#000000' };
    case 'com.trader.ninjatrader.targetplus':
      return { title: 'QTY_TARGET_UP', subtitle: '1', bgColor: '#FFFFFF', textColor: '#000000' };
    case 'com.trader.ninjatrader.targetminus':
      return { title: 'QTY_TARGET_DN', subtitle: '1', bgColor: '#FFFFFF', textColor: '#000000' };
    case 'com.trader.ninjatrader.beplus':
      return { title: 'QTY_BE_UP', subtitle: '1', bgColor: '#FFFFFF', textColor: '#000000' };
    case 'com.trader.ninjatrader.beminus':
      return { title: 'QTY_BE_DN', subtitle: '1', bgColor: '#FFFFFF', textColor: '#000000' };
    case 'com.trader.ninjatrader.qtyplus':
      return { title: 'QTY_PLUS', subtitle: `${qty}`, bgColor: '#FFFFFF', textColor: '#000000' };
    case 'com.trader.ninjatrader.qtyminus':
      return { title: 'QTY_MINUS', subtitle: `${qty}`, bgColor: '#FFFFFF', textColor: '#000000' };
    case 'com.trader.ninjatrader.qtyreset':
      return { title: 'QTY_RESET', subtitle: `${defQty}`, bgColor: '#FFFFFF', textColor: '#000000' };

    case 'com.trader.ninjatrader.instrument': {
      const cfgInstrument = (settings.instrument as string) || '';
      if (!cfgInstrument) {
        return { title: '---', subtitle: 'Config requis', bgColor: Colors.instrumentIndigo, textColor: Colors.textDim };
      }
      const displayLabel = (settings.displayLabel as string) || cfgInstrument;
      // Correspondance sur la racine : « MNQ » correspond à « MNQ 06-25 », ou égalité stricte.
      const stateInst = state.instrument || '';
      const isActive = stateInst === cfgInstrument || stateInst.startsWith(cfgInstrument + ' ');
      let pctText = '';
      const info = state.instrumentInfo;
      if (isActive && info && info.lastPrice > 0) {
        const refPrice = info.settlementPrice > 0 ? info.settlementPrice : info.openPrice;
        if (refPrice > 0) {
          const pct = ((info.lastPrice - refPrice) / refPrice) * 100;
          pctText = `${pct >= 0 ? '+' : ''}${pct.toFixed(2)}%`;
        }
      }
      return {
        title: displayLabel,
        subtitle: isActive ? (pctText || 'ACTIVE') : 'INACTIVE',
        bgColor: isActive ? Colors.instrumentActive : Colors.disabled,
        textColor: isActive ? Colors.textGold : Colors.textDim,
      };
    }
    case 'com.trader.ninjatrader.account': {
      const currentAccount = state.ntConnected ? (state.account || '') : '';
      const isActive = connected && currentAccount !== '';
      return {
        title: formatAccountLabel(currentAccount),
        subtitle: isActive ? 'ACTIVE' : 'INACTIVE',
        bgColor: isActive ? Colors.instrumentActive : Colors.disabled,
        textColor: isActive ? Colors.textGold : Colors.textDim,
      };
    }
    case 'com.trader.ninjatrader.status': {
      const statusType = (settings.statusType as StatusType) || 'connection';
      const { title, subtitle } = getDisplayText(statusType, state);
      let bgColor: string;
      switch (statusType) {
        case 'account': bgColor = Colors.statusDark; break;
        case 'instrument': bgColor = Colors.instrumentIndigo; break;
        case 'position': {
          const dir = pos?.direction;
          bgColor = dir === 'Long' ? Colors.buyGreen : dir === 'Short' ? Colors.sellRed : Colors.statusDark;
          break;
        }
        case 'pnl': {
          const pnl = pos?.unrealizedPnl ?? 0;
          bgColor = pnl > 0 ? Colors.buyGreen : pnl < 0 ? Colors.sellRed : Colors.statusDark;
          break;
        }
        case 'quantity': bgColor = Colors.qtySlate; break;
        case 'connection': bgColor = state.ntConnected ? Colors.buyGreen : Colors.sellRed; break;
        case 'safety': {
          const safety = state.safety;
          bgColor = !safety?.armed ? Colors.disabled : safety.entriesBlocked ? Colors.sellRed : Colors.buyGreen;
          break;
        }
        default: bgColor = Colors.statusDark;
      }
      return { title, subtitle, bgColor, textColor: Colors.textWhite };
    }
    case 'com.trader.ninjatrader.cooldown': {
      const enabled = state.cooldownEnabled ?? false;
      const active = state.cooldownActive ?? false;
      const secs = state.cooldownSecondsRemaining ?? 0;
      if (active) {
        return { title: 'COUNTDOWN', subtitle: `${secs}`, bgColor: Colors.sellRed, textColor: '#FFFFFF' };
      }
      // Active, la touche affiche la durée réglée plutôt qu'un « ON » que le fond vert dit déjà.
      // C'est la seule façon de vérifier d'un coup d'œil, avant la séance, combien de temps la
      // temporisation bloquera réellement — la valeur vient du bridge, qui l'applique, et non du
      // réglage local, qui pourrait ne pas lui avoir été transmis.
      return {
        title: 'SEC', subtitle: enabled ? formatDuree(state.cooldownSeconds ?? 60) : 'OFF',
        bgColor: enabled ? Colors.buyGreen : Colors.disabled,
        textColor: enabled ? '#FFFFFF' : Colors.textDim,
      };
    }
    case 'com.trader.ninjatrader.safety': {
      const safety = state.safety ?? DEFAULT_SAFETY_STATUS;
      // Le mode développement lève la seule garantie de cette macro : il doit se voir sur la
      // touche elle-même, pas uniquement dans un formulaire de réglages qu'on n'ouvre jamais.
      const dev = settings.devMode === true;
      const marque = dev ? { badge: 'DEV', badgeColor: Colors.cancelYellow } : {};

      // Un ordre passé à la main dans NinjaTrader vient d'être annulé. Prime sur tout le reste :
      // c'est la seule chose qui, à cet instant, mérite le regard du trader.
      if (ctx.lastViolationAt && Date.now() - ctx.lastViolationAt < VIOLATION_BANNER_MS) {
        return {
          ...marque,
          title: 'SAFETY:STOP', subtitle: 'MANUEL',
          detail: 'ORDRE ANNULE',
          bgColor: Colors.sellRed, textColor: Colors.textWhite,
        };
      }

      if (!safety.armed) {
        // Pas de rappel des limites configurées ici : la touche sert à savoir si la protection
        // est active, pas à relire des réglages qu'on consulte dans l'interface.
        return {
          ...marque,
          title: 'SAFETY:GUARD', subtitle: 'OFF',
          bgColor: Colors.disabled, textColor: Colors.textDim,
        };
      }

      // Armée et une limite atteinte — les entrées sont refusées jusqu'à la fin du verrou.
      if (safety.entriesBlocked) {
        return {
          ...marque,
          title: safety.blockReason === 'dailyLoss' ? 'SAFETY:LOSS' : 'SAFETY:MAX',
          subtitle: formatLockRemaining(safety.lockSecondsRemaining),
          detail: formatSafetyDetail(safety),
          bgColor: Colors.sellRed, textColor: Colors.textWhite,
        };
      }

      // Anti-Tilt en cours. Jaune et non rouge, délibérément : le rouge de la touche veut dire
      // « refusé » partout ailleurs, et ici rien n'est refusé — les entrées partent encore, il faut
      // seulement les tenir. Placé après le blocage réel, qui prime parce qu'aucun appui ne le lèvera.
      if (safety.tiltActive) {
        return {
          ...marque,
          title: 'SAFETY:TILT',
          // Un épisode a un minuteur ; une condition contextuelle n'en a pas — elle dure autant que
          // la position qui l'a produite. Afficher « 0s » laisserait croire qu'elle vient de finir.
          subtitle: safety.tiltSecondsRemaining > 0 ? formatLockRemaining(safety.tiltSecondsRemaining) : 'HOLD',
          detail: motifTilt(safety.tiltReason),
          bgColor: Colors.cancelYellow, textColor: '#000000',
        };
      }

      // Armée et rien à signaler. Le fond vert dit déjà que la protection est active : la place
      // de la deuxième ligne sert donc à ce qui change, le temps de verrou restant, plutôt qu'à
      // un « ON » qui répète la couleur.
      //
      // Le compte à rebours s'affiche aussi en mode développement. Il y a bien une nuance — le
      // verrou peut alors être levé d'un appui, la durée n'engage donc à rien — mais c'est le
      // badge DEV qui porte cet avertissement, l'écrire une seconde fois ne l'ajoutait pas.
      return {
        ...marque,
        title: 'SAFETY:GUARD',
        subtitle: formatLockRemaining(safety.lockSecondsRemaining),
        detail: formatSafetyRestant(safety),
        bgColor: Colors.buyGreen, textColor: Colors.textWhite,
      };
    }

    // Automatisme propre à l'hôte : il n'existe pas de commande « auto BE » côté bridge, c'est
    // l'hôte qui surveille le gain et déclenche un `breakeven` ordinaire.
    case 'host.autobe': {
      const declenchement = Number(settings.triggerTicks) || 0;
      if (!ctx.autoBe.actif) {
        return {
          title: 'AUTOBE', subtitle: 'OFF',
          bgColor: Colors.disabled, textColor: Colors.textDim,
        };
      }
      if (ctx.autoBe.pose) {
        return { title: 'AUTOBE', subtitle: 'POSE', bgColor: Colors.buyGreen, textColor: Colors.textWhite };
      }
      // Armé et en attente : afficher la progression vers le seuil est ce qui permet de savoir
      // que l'automatisme suit réellement la position.
      const gain = gainEnTicks(state);
      const attente = gain === null ? 'ARME' : `${Math.floor(gain)}/${declenchement}`;
      return { title: 'AUTOBE', subtitle: attente, bgColor: Colors.beBlue, textColor: Colors.textWhite };
    }

    // Action propre à l'hôte : la navigation entre pages était assurée par les touches
    // « dossier » natives d'Elgato, qui n'existent plus ici.
    case 'host.navigate': {
      const cible = String(settings.targetPage ?? 'next');
      // Pas de chevrons ni d'esperluette ici : ces caractères cassent le SVG où le texte est
      // injecté brut.
      const auto = cible === 'next' ? 'SUIV' : cible === 'prev' ? 'PREC' : `P${cible}`;
      const detail = cible === 'next' ? 'suivante' : cible === 'prev' ? 'precedente' : `page ${cible}`;
      return {
        title: (settings.label as string) || auto,
        subtitle: detail,
        bgColor: Colors.reverseViolet,
        textColor: '#FFFFFF',
      };
    }

    default:
      return null;
  }
}
