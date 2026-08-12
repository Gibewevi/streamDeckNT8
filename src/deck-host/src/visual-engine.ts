/**
 * Calcul du visuel d'une touche à partir de l'action, de ses réglages et de l'état de trading.
 *
 * Port fidèle de `computeVisual` (`src/streamdeck-ninjatrader/src/plugin.ts:261-500`). Les
 * commentaires expliquant un choix d'affichage sont conservés : ils décrivent des règles de
 * sécurité, pas du style.
 */
import { Colors, ButtonVisual } from './visuals.js';
import {
  TradingState, SafetyStatus, DEFAULT_SAFETY_STATUS, DEFAULT_TREND_STATE, TrendDirection,
} from './messages.js';
import { formatAccountLabel, getDisplayText, StatusType } from './status-display.js';

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

/**
 * Contexte « rien n'est branché », pendant de `DISCONNECTED_STATE`.
 *
 * Inutilisé par l'hôte, qui a toujours un contexte réel. Il existe pour l'éditeur Bitlearn, et il
 * vit ici plutôt que là-bas pour une raison précise : le jour où `VisualContext` gagne un champ,
 * le compilateur de l'hôte le signale immédiatement. Défini côté Bitlearn, il aurait dérivé en
 * silence, ce qui est exactement le défaut que ce partage supprime.
 */
export const RESTING_CONTEXT: VisualContext = {
  bridgeConnected: false,
  lastRejectionAt: null,
  lastViolationAt: null,
  defaultQuantity: 1,
  autoBe: { actif: false, pose: false },
};

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

/**
 * Décompte qui s'égrène, en `m:ss` — pour une attente que l'on subit et dont on veut voir la fin
 * approcher.
 *
 * `formatLockRemaining` arrondit à la minute au-dessus de 60 s : une pause de dix minutes affichait
 * « 10m » pendant soixante secondes, puis « 9m ». Rien ne bougeait à l'écran neuf minutes durant,
 * et la touche paraissait figée alors que la valeur, elle, était bien rafraîchie cinq fois par
 * seconde. On ne pouvait pas savoir s'il restait neuf minutes ou neuf minutes cinquante-neuf.
 *
 * Les minutes ne sont pas bornées à 59 : une pause va jusqu'à 120 min, et « 119:59 » se lit mieux
 * que « 1h59:59 » sur une touche de 72 pixels.
 */
function formatDecompte(seconds: number): string {
  const total = Math.max(0, Math.floor(seconds));
  return `${Math.floor(total / 60)}:${String(total % 60).padStart(2, '0')}`;
}

/**
 * Préavis avant la pause obligatoire. Un quart d'heure : de quoi terminer la position en cours et
 * ne pas en ouvrir une nouvelle, sans occuper la touche pendant toute la séance.
 */
const PAUSE_PREAVIS_S = 15 * 60;

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
 * Sens d'une unité de temps, en deux caractères — la ligne du bas d'une touche de 144 pixels doit
 * porter DEUX unités et leurs sens. `--` pour neutre, comme partout ailleurs sur le deck.
 */
function abrege(direction: TrendDirection | ''): string {
  if (direction === 'up') return 'UP';
  if (direction === 'down') return 'DN';
  return '--';
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
 * lever. Le `tone` ci-dessous distingue un blocage réel d'un simple avis transitoire : une touche
 * éteinte ne doit jamais vouloir dire « votre ordre a été rejeté ».
 */
/**
 * `tone` distingue trois situations que le seul libellé ne suffit pas à séparer d'un coup d'œil :
 *
 *   refuse — Guard ou la temporisation refusent : rouge franc, c'est exceptionnel et coûteux à manquer ;
 *   muted  — le plafond de contrats est atteint : la touche ne part pas, mais détenir sa taille
 *            habituelle n'est PAS un incident. Un rouge permanent à chaque position finirait par
 *            ne plus rien vouloir dire, et masquerait un vrai blocage le jour où il arrive ;
 *   notice — avis non bloquant (appui long anti-tilt, rejet NT8) : la touche part encore.
 */
type EntryStatus = { label: string; tone: 'refuse' | 'muted' | 'notice' } | null;

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
    // La pause ne refuse que ce qui CRÉE de l'exposition. La touche qui permet de sortir reste
    // fonctionnelle côté bridge — l'afficher en rouge ferait croire au trader qu'il est enfermé
    // dans sa position, et c'est cette croyance, pas le blocage, qui coûte cher : on n'essaie pas
    // une touche qu'on croit morte.
    const sortiePossible =
      state.safety.blockReason === 'mandatoryPause' && !augmenteExposition(actionId, state);

    if (!sortiePossible) {
      // Le motif décide du libellé : « MAX TRADES » sur une pause obligatoire enverrait le trader
      // chercher une limite qu'il n'a pas atteinte, au lieu de lui dire d'aller se lever.
      const motifs: Record<string, string> = {
        dailyLoss: 'LOSS LIMIT',
        mandatoryPause: 'PAUSE',
        tradeLimit: 'MAX TRADES',
      };
      return {
        label: motifs[state.safety.blockReason] ?? 'MAX TRADES',
        tone: 'refuse',
      };
    }
  }
  // Plafond de contrats atteint. Seules les touches qui feraient GROSSIR la position réagissent :
  // celle qui permet d'en sortir reste intacte, sans quoi on croirait y être coincé.
  if (state.safety?.atContractCap && augmenteExposition(actionId, state)) {
    return { label: 'MAX QTY', tone: 'muted' };
  }
  if (state.cooldownActive) return { label: 'BLOCKED', tone: 'refuse' };
  // Anti-Tilt : la touche fonctionne toujours, elle exige seulement d'être tenue. Jamais grisée
  // donc — le gris est réservé à ce qui ne part pas — et affichée après les deux vrais blocages,
  // qui priment parce qu'aucun maintien ne les lèvera.
  if (tiltAppliesTo(actionId, state)) {
    return { label: `HOLD ${state.safety.tiltHoldSeconds}s`, tone: 'notice' };
  }
  // NinjaTrader a refusé le dernier ordre : on prévient, mais la touche reste utilisable.
  if (ctx.lastRejectionAt && Date.now() - ctx.lastRejectionAt < REJECTION_BANNER_MS) {
    return { label: 'REJECTED', tone: 'notice' };
  }
  return null;
}

/**
 * Visuel d'une touche d'entrée.
 *
 * Achat et vente ne se distinguent plus par la teinte mais par l'INVERSION : l'achat est orange
 * plein à texte blanc, la vente est noire à titre orange. Libérer le vert et le rouge est ce qui
 * permet au rouge de ne plus signifier que le refus, partout sur le deck.
 */
function entryVisual(
  titre: string,
  sens: 'Buy' | 'Sell',
  qty: number,
  state: TradingState,
  ctx: VisualContext,
  actionId: string,
): ButtonVisual {
  const st = entryStatus(state, ctx, actionId);

  // Refus : la touche ne partira pas. Seul endroit de la charte où une couleur d'alerte subsiste.
  if (st?.tone === 'refuse') {
    return { title: titre, subtitle: st.label, bgColor: Colors.refuse, textColor: Colors.textWhite };
  }

  // Plafond atteint : la touche ne part pas non plus, mais sans alarme. Elle s'éteint.
  if (st?.tone === 'muted') {
    return { title: titre, subtitle: st.label, bgColor: Colors.black, textColor: Colors.textDim, subtitleColor: Colors.textDim };
  }

  const connected = ctx.bridgeConnected && state.ntConnected;
  const accent = connected ? Colors.orange : Colors.orangeDim;
  const achat = sens === 'Buy';

  // Un avis non bloquant (HOLD, REJECTED) remplace la quantité : la touche part encore, et c'est
  // l'avis qui doit se lire, pas la taille.
  const base = st?.label
    ? { subtitle: st.label, subtitleAccent: undefined }
    : { subtitle: `${sens} `, subtitleAccent: `×${qty}` };

  return achat
    ? {
        title: titre, ...base,
        bgColor: accent,
        textColor: Colors.textWhite,
        subtitleColor: Colors.textWhite,
        accentColor: Colors.textWhite,
      }
    : {
        title: titre, ...base,
        bgColor: Colors.black,
        textColor: accent,
        subtitleColor: connected ? Colors.textWhite : Colors.textDim,
        accentColor: accent,
      };
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
    case 'com.trader.ninjatrader.buymarket':
      return entryVisual('MKT', 'Buy', qty, state, ctx, actionId);
    case 'com.trader.ninjatrader.sellmarket':
      return entryVisual('MKT', 'Sell', qty, state, ctx, actionId);
    case 'com.trader.ninjatrader.buylimit':
      return entryVisual('LMT', 'Buy', qty, state, ctx, actionId);
    case 'com.trader.ninjatrader.selllimit':
      return entryVisual('LMT', 'Sell', qty, state, ctx, actionId);
    // « FLAT » et non « Close » : la touche voisine (cancelOrders) affiche déjà CLOSE, et deux
    // libellés identiques sur des commandes différentes est un piège en séance.
    case 'com.trader.ninjatrader.flatten':
      return { title: 'FLAT', subtitle: `Qty ${pos?.quantity ?? 0}`, bgColor: Colors.white, textColor: Colors.textBlack };

    case 'com.trader.ninjatrader.cancelorders': {
      // Orange et non rouge quand une position existe : c'est une touche qui AGIT, et le rouge de
      // la charte ne désigne que ce qui refuse de partir.
      const posQty = Math.abs(pos?.quantity ?? 0);
      return {
        title: 'QTY_CANCEL', subtitle: posQty > 0 ? `${posQty}` : '0',
        bgColor: posQty > 0 ? Colors.orange : Colors.white,
        textColor: posQty > 0 ? Colors.textWhite : Colors.textBlack,
      };
    }
    case 'com.trader.ninjatrader.cancelworkingorders': {
      // Annule uniquement les ordres en attente — la position n'est pas touchée.
      const orders = pos?.activeOrderCount ?? 0;
      return {
        title: 'CANCEL', subtitle: orders > 0 ? `${orders} order${orders > 1 ? 's' : ''}` : 'none',
        bgColor: orders > 0 ? Colors.orange : Colors.black,
        textColor: orders > 0 ? Colors.textWhite : Colors.textDim,
      };
    }
    case 'com.trader.ninjatrader.reverse': {
      // Inverser ouvre une position dans l'autre sens : la macro de sécurité s'applique aussi.
      const st = entryStatus(state, ctx, actionId);
      return {
        title: 'Invert', subtitle: st?.label ?? `Qty ${pos?.quantity ?? 0}`,
        bgColor: st?.tone === 'refuse' ? Colors.refuse : st?.tone === 'muted' ? Colors.black : Colors.white,
        textColor: st?.tone === 'refuse' ? Colors.textWhite : st?.tone === 'muted' ? Colors.textDim : Colors.textBlack,
      };
    }
    case 'com.trader.ninjatrader.breakeven': {
      const offset = (settings.offsetTicks as number) ?? 0;
      return { title: 'BE', subtitle: `+${offset}`, bgColor: Colors.orange, textColor: Colors.textWhite };
    }

    case 'com.trader.ninjatrader.stopplus':
      return { title: 'QTY_STOP_UP', subtitle: '1', bgColor: Colors.white, textColor: Colors.textBlack };
    case 'com.trader.ninjatrader.stopminus':
      return { title: 'QTY_STOP_DN', subtitle: '1', bgColor: Colors.white, textColor: Colors.textBlack };
    case 'com.trader.ninjatrader.targetplus':
      return { title: 'QTY_TARGET_UP', subtitle: '1', bgColor: Colors.white, textColor: Colors.textBlack };
    case 'com.trader.ninjatrader.targetminus':
      return { title: 'QTY_TARGET_DN', subtitle: '1', bgColor: Colors.white, textColor: Colors.textBlack };
    case 'com.trader.ninjatrader.beplus':
      return { title: 'QTY_BE_UP', subtitle: '1', bgColor: Colors.white, textColor: Colors.textBlack };
    case 'com.trader.ninjatrader.beminus':
      return { title: 'QTY_BE_DN', subtitle: '1', bgColor: Colors.white, textColor: Colors.textBlack };
    case 'com.trader.ninjatrader.qtyplus':
      return { title: 'QTY_PLUS', subtitle: `${qty}`, bgColor: Colors.white, textColor: Colors.textBlack };
    case 'com.trader.ninjatrader.qtyminus':
      return { title: 'QTY_MINUS', subtitle: `${qty}`, bgColor: Colors.white, textColor: Colors.textBlack };
    case 'com.trader.ninjatrader.qtyreset':
      return { title: 'QTY_RESET', subtitle: `${defQty}`, bgColor: Colors.white, textColor: Colors.textBlack };

    case 'com.trader.ninjatrader.instrument': {
      const cfgInstrument = (settings.instrument as string) || '';
      if (!cfgInstrument) {
        return { title: '---', subtitle: 'Config requis', bgColor: Colors.black, textColor: Colors.textDim };
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
        bgColor: isActive ? Colors.orange : Colors.black,
        textColor: Colors.textWhite,
      };
    }
    case 'com.trader.ninjatrader.account': {
      const currentAccount = state.ntConnected ? (state.account || '') : '';
      const isActive = connected && currentAccount !== '';
      return {
        title: formatAccountLabel(currentAccount, 'ACCT'),
        subtitle: isActive ? 'ACTIVE' : 'INACTIVE',
        bgColor: isActive ? Colors.orange : Colors.black,
        textColor: Colors.textWhite,
      };
    }
    case 'com.trader.ninjatrader.status': {
      const statusType = (settings.statusType as StatusType) || 'connection';
      const { title, subtitle } = getDisplayText(statusType, state);
      // Indicateurs : orange quand il y a quelque chose en cours, noir au repos. Le P&L et la
      // position abandonnent le vert/rouge — le signe et le chiffre portent seuls le résultat,
      // et le rouge reste ainsi réservé à ce qui refuse de partir.
      let bgColor: string;
      switch (statusType) {
        case 'position': bgColor = pos?.exists ? Colors.orange : Colors.black; break;
        case 'pnl': bgColor = pos?.exists ? Colors.orange : Colors.black; break;
        case 'instrument': bgColor = state.instrument ? Colors.orange : Colors.black; break;
        case 'account': bgColor = connected && state.account ? Colors.orange : Colors.black; break;
        case 'quantity': bgColor = Colors.black; break;
        // La déconnexion de NinjaTrader est le seul état d'indicateur qui mérite le rouge : rien
        // ne peut plus partir, ce qui est bien un refus — et le manquer coûte une séance.
        case 'connection': bgColor = state.ntConnected ? Colors.orange : Colors.refuse; break;
        case 'safety': {
          const safety = state.safety;
          bgColor = !safety?.armed ? Colors.black : safety.entriesBlocked ? Colors.refuse : Colors.orange;
          break;
        }
        default: bgColor = Colors.black;
      }
      return { title, subtitle, bgColor, textColor: Colors.textWhite };
    }
    case 'com.trader.ninjatrader.cooldown': {
      const enabled = state.cooldownEnabled ?? false;
      const active = state.cooldownActive ?? false;
      const secs = state.cooldownSecondsRemaining ?? 0;
      if (active) {
        return { title: 'COUNTDOWN', subtitle: `${secs}`, bgColor: Colors.refuse, textColor: Colors.textWhite };
      }
      // Active, la touche affiche la durée réglée plutôt qu'un « ON » que le fond vert dit déjà.
      // C'est la seule façon de vérifier d'un coup d'œil, avant la séance, combien de temps la
      // temporisation bloquera réellement — la valeur vient du bridge, qui l'applique, et non du
      // réglage local, qui pourrait ne pas lui avoir été transmis.
      return {
        title: 'SEC', subtitle: enabled ? formatDuree(state.cooldownSeconds ?? 60) : 'OFF',
        bgColor: enabled ? Colors.orange : Colors.black,
        textColor: Colors.textWhite,
      };
    }
    // Tendance — AFFICHAGE SEUL dans cette version. Aucune couleur de refus n'apparaît ici, et
    // c'est délibéré à deux titres : la macro ne refuse rien, et même quand elle le fera, une
    // tendance baissière n'est pas un refus — les ventes, elles, seront autorisées. Le rouge se
    // lira sur les touches d'entrée concernées, là où il veut dire quelque chose.
    //
    // Hausse et baisse se distinguent par l'INVERSION, exactement comme Achat et Vente : orange
    // plein contre noir à symbole orange. C'est le même geste de lecture, déjà appris.
    case 'com.trader.ninjatrader.trend': {
      const trend = state.trend ?? DEFAULT_TREND_STATE;

      if (!connected || !trend.available) {
        return {
          title: 'TREND:FLAT', subtitle: 'NO DATA',
          bgColor: Colors.black, textColor: Colors.textDim, subtitleColor: Colors.textDim,
        };
      }

      // Les deux unités séparément, toujours : c'est ce qui rend un FLAT lisible. Sans elles, une
      // touche neutre ne dit pas si le marché hésite ou si les deux unités se contredisent, et le
      // trader n'a aucun moyen de le savoir depuis le boîtier.
      const detail = trend.higher
        ? `${trend.referenceMinutes}m ${abrege(trend.reference)} · ${trend.higherMinutes}m ${abrege(trend.higher)}`
        : `${trend.referenceMinutes}m ${abrege(trend.reference)}`;

      if (trend.direction === 'up') {
        return {
          title: 'TREND:UP', subtitle: detail,
          bgColor: Colors.orange, textColor: Colors.textWhite, subtitleColor: Colors.textWhite,
        };
      }
      if (trend.direction === 'down') {
        return {
          title: 'TREND:DOWN', subtitle: detail,
          bgColor: Colors.black, textColor: Colors.orange, subtitleColor: Colors.textWhite,
        };
      }
      return {
        title: 'TREND:FLAT', subtitle: detail,
        bgColor: Colors.black, textColor: Colors.textDim, subtitleColor: Colors.textDim,
      };
    }

    // Pause obligatoire — macro à part, indépendante de l'armement de Guard.
    //
    // Trois états, et le troisième est le plus important : le décompte AVANT la pause. Une coupure
    // qui tombe sans prévenir se découvre au moment où l'on voulait entrer, et c'est ainsi qu'on
    // finit par désactiver la règle. Annoncée, elle se prépare — on termine la position en cours
    // au lieu d'en ouvrir une.
    case 'com.trader.ninjatrader.pause': {
      const safety = state.safety ?? DEFAULT_SAFETY_STATUS;
      const reglee = Number(settings.pauseAfterMinutes) > 0;

      if (!reglee) {
        return { title: 'PAUSE', subtitle: 'OFF', bgColor: Colors.black, textColor: Colors.textWhite };
      }

      // La pause s'applique : rouge, comme tout ce qui refuse une entrée.
      //
      // Décompte à la seconde, et non arrondi à la minute : c'est le seul moment où l'on regarde
      // cette touche en attendant qu'elle libère, et une durée qui ne bouge pas ne dit pas si la
      // règle tourne encore. Même raison que le compte à rebours de la Temporisation.
      if (safety.pauseActive) {
        return {
          title: 'PAUSE',
          subtitle: formatDecompte(safety.pauseSecondsRemaining),
          detail: 'REPOS',
          bgColor: Colors.refuse, textColor: Colors.textWhite,
        };
      }

      // Le compteur ne tourne pas encore : aucun trade pris depuis la dernière coupure. C'est un
      // état normal, pas une règle inactive — le dire évite de croire que le réglage n'a pas pris.
      if (safety.pauseDueInSeconds <= 0) {
        return {
          title: 'PAUSE', subtitle: 'PRET',
          detail: `${Math.round(Number(settings.pauseDurationMinutes) || 0)}min`,
          bgColor: Colors.black, textColor: Colors.orange, subtitleColor: Colors.textWhite,
        };
      }

      // Décompte. Orange plein dans le dernier quart d'heure : c'est le moment où l'information
      // change de nature — elle cesse d'être un rappel et devient une consigne.
      const imminente = safety.pauseDueInSeconds <= PAUSE_PREAVIS_S;
      return {
        title: 'PAUSE',
        subtitle: formatLockRemaining(safety.pauseDueInSeconds),
        detail: 'RESTANT',
        bgColor: imminente ? Colors.orange : Colors.black,
        textColor: Colors.textWhite,
        subtitleColor: imminente ? Colors.textWhite : Colors.orange,
      };
    }

    case 'com.trader.ninjatrader.safety': {
      const safety = state.safety ?? DEFAULT_SAFETY_STATUS;
      // Aucun badge de mode ici : il n'existe plus de réglage qui affaiblisse cette macro, donc
      // plus rien à signaler d'un coup d'œil. Ce que la touche montre est ce qui s'applique.

      // Un ordre passé à la main dans NinjaTrader vient d'être annulé. Prime sur tout le reste :
      // c'est la seule chose qui, à cet instant, mérite le regard du trader.
      if (ctx.lastViolationAt && Date.now() - ctx.lastViolationAt < VIOLATION_BANNER_MS) {
        return {
          title: 'SAFETY:STOP', subtitle: 'MANUEL',
          detail: 'ORDRE ANNULE',
          bgColor: Colors.refuse, textColor: Colors.textWhite,
        };
      }

      if (!safety.armed) {
        // Pas de rappel des limites configurées ici : la touche sert à savoir si la protection
        // est active, pas à relire des réglages qu'on consulte dans l'interface.
        return {
          title: 'SAFETY:GUARD', subtitle: 'OFF',
          bgColor: Colors.black, textColor: Colors.textWhite,
        };
      }

      // La liquidation a échoué : des positions peuvent être encore ouvertes alors que tout
      // annonce la fin de journée. Prime sur absolument tout le reste, y compris le blocage —
      // c'est le seul état où le trader doit agir immédiatement à la main.
      if (safety.autoFlattenFailed) {
        return {
          title: 'SAFETY:ERR', subtitle: 'LIQUID.',
          detail: 'VERIFIER NT',
          bgColor: Colors.refuse, textColor: Colors.textWhite,
        };
      }

      // Le seuil est franchi, la tolérance s'écoule. Annoncé pour qu'il reste possible de fermer
      // à ses conditions : quelques secondes de préavis valent mieux qu'une liquidation surprise,
      // et la règle n'y perd rien puisque le délai s'écoule de toute façon.
      if (safety.autoFlattenPending) {
        return {
          title: 'SAFETY:LIQUID',
          subtitle: `${safety.autoFlattenSecondsRemaining}s`,
          detail: formatSafetyDetail(safety),
          bgColor: Colors.refuse, textColor: Colors.textWhite,
        };
      }

      // Pause obligatoire. Rouge comme les autres blocages — les entrées sont bien refusées — mais
      // avec SON décompte et non celui du verrou : ici le trader attend la fin de la pause, pas la
      // fin du verrou, et afficher les heures du verrou lui ferait croire sa séance terminée.
      // Placée avant le cas général, qui n'a aucune minuterie propre à montrer.
      // Même décompte à la seconde que la touche Pause : c'est la même attente, et deux touches
      // voisines qui annoncent « 9m » et « 9:58 » pour la même chose se contrediraient.
      if (safety.entriesBlocked && safety.blockReason === 'mandatoryPause') {
        return {
          title: 'SAFETY:PAUSE',
          subtitle: formatDecompte(safety.pauseSecondsRemaining),
          detail: 'REPOS',
          bgColor: Colors.refuse, textColor: Colors.textWhite,
        };
      }

      // Armée et une limite atteinte — les entrées sont refusées jusqu'à la fin du verrou.
      if (safety.entriesBlocked) {
        return {
          title: safety.blockReason === 'dailyLoss' ? 'SAFETY:LOSS' : 'SAFETY:MAX',
          subtitle: formatLockRemaining(safety.lockSecondsRemaining),
          detail: formatSafetyDetail(safety),
          bgColor: Colors.refuse, textColor: Colors.textWhite,
        };
      }

      // Anti-Tilt en cours. Ni rouge ni orange plein, délibérément : le rouge veut dire « refusé »
      // partout ailleurs et ici rien ne l'est — les entrées partent encore, il faut seulement les
      // tenir. L'inversion (noir à texte orange) la distingue aussi bien du blocage rouge que de
      // la macro simplement armée. Placée après le blocage réel, qui prime : aucun appui ne le lève.
      if (safety.tiltActive) {
        return {
          title: 'SAFETY:TILT',
          // Un épisode a un minuteur ; une condition contextuelle n'en a pas — elle dure autant que
          // la position qui l'a produite. Afficher « 0s » laisserait croire qu'elle vient de finir.
          subtitle: safety.tiltSecondsRemaining > 0 ? formatLockRemaining(safety.tiltSecondsRemaining) : 'HOLD',
          detail: motifTilt(safety.tiltReason),
          bgColor: Colors.black, textColor: Colors.orange, subtitleColor: Colors.textWhite,
        };
      }

      // Armée et rien à signaler. Le fond orange dit déjà que la protection est active : la place
      // de la deuxième ligne sert donc à ce qui change, le temps de verrou restant, plutôt qu'à
      // un « ON » qui répète la couleur. Ce compte à rebours engage : rien ne permet plus de
      // l'abréger.
      //
      // La pause qui approche prend la troisième ligne le temps de son dernier quart d'heure. Une
      // coupure qui tombe sans prévenir se subit — on la découvre au moment où l'on voulait entrer,
      // et c'est ainsi qu'on finit par désarmer la protection. Annoncée, elle se prépare : on
      // termine la position en cours au lieu d'en ouvrir une.
      const pauseImminente = safety.pauseDueInSeconds > 0 && safety.pauseDueInSeconds <= PAUSE_PREAVIS_S;

      return {
        title: 'SAFETY:GUARD',
        subtitle: formatLockRemaining(safety.lockSecondsRemaining),
        detail: pauseImminente
          ? `PAUSE ${formatLockRemaining(safety.pauseDueInSeconds)}`
          : formatSafetyRestant(safety),
        bgColor: Colors.orange, textColor: Colors.textWhite,
      };
    }

    // Automatisme propre à l'hôte : il n'existe pas de commande « auto BE » côté bridge, c'est
    // l'hôte qui surveille le gain et déclenche un `breakeven` ordinaire.
    case 'host.autobe': {
      const declenchement = Number(settings.triggerTicks) || 0;
      if (!ctx.autoBe.actif) {
        return {
          title: 'AUTOBE', subtitle: 'OFF',
          bgColor: Colors.black, textColor: Colors.textWhite,
        };
      }
      if (ctx.autoBe.pose) {
        return { title: 'AUTOBE', subtitle: 'POSE', bgColor: Colors.orange, textColor: Colors.textWhite };
      }
      // Armé et en attente : afficher la progression vers le seuil est ce qui permet de savoir
      // que l'automatisme suit réellement la position.
      const gain = gainEnTicks(state);
      const attente = gain === null ? 'ARME' : `${Math.floor(gain)}/${declenchement}`;
      return { title: 'AUTOBE', subtitle: attente, bgColor: Colors.orange, textColor: Colors.textWhite };
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
        bgColor: Colors.black,
        textColor: Colors.textWhite,
      };
    }

    default:
      return null;
  }
}
