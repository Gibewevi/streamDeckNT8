/**
 * Message types matching the bridge protocol V1.
 */

export interface BridgeMessage {
  type: 'command' | 'response' | 'event' | 'error';
  version: string;
  requestId: string | null;
  timestamp: string;
  source: string;
  action: string;
  payload?: Record<string, unknown>;
  result?: { success: boolean; [key: string]: unknown };
  error?: { code: string; message: string };
}

export interface TradingState {
  account: string;
  instrument: string;
  quantity: number;
  defaultQuantity: number;
  ntConnected: boolean;
  pluginConnected: boolean;
  position: PositionState | null;
  instrumentInfo: InstrumentInfo | null;
  availableAccounts: string[];
  /**
   * Cash value du compte, quand NinjaTrader l'expose. Sert au journal Bitlearn à partir du capital
   * réel : le P&L dit ce qui a changé, jamais à partir de quoi, et sans ce point de départ tout
   * pourcentage affiché est faux.
   */
  cashValue?: number | null;
  cooldownEnabled: boolean;
  cooldownActive: boolean;
  cooldownSecondsRemaining: number;
  /** Durée configurée, appliquée au prochain trade perdant — à ne pas confondre avec le décompte. */
  cooldownSeconds: number;
  safety: SafetyStatus;
}

/**
 * State of the lockable safety macro, as published by the bridge.
 * The bridge owns and enforces these rules — the plugin only displays them.
 */
export interface SafetyStatus {
  armed: boolean;
  /** True while the macro cannot be disarmed. */
  locked: boolean;
  lockSecondsRemaining: number;
  lockDurationHours: number;
  /** Max trades allowed once the session P&L is negative. 0 = rule off. */
  maxTradesWhenLosing: number;
  /** Max session loss, positive number. 0 = rule off. */
  dailyLossLimit: number;
  /** Contracts the account may hold. 0 = rule off. */
  maxContracts: number;
  /** Position already at the cap: anything that grows it is refused. Not an incident — only the
   *  keys that would add to the position react to it, the Safety key stays as it was. */
  atContractCap: boolean;
  tradeCount: number;
  sessionPnl: number;
  /** False when NinjaTrader does not expose account P&L — the loss rules are then inert. */
  pnlAvailable: boolean;
  /** True when the bridge is currently refusing position-opening actions. */
  entriesBlocked: boolean;
  blockReason: '' | 'dailyLoss' | 'tradeLimit' | 'mandatoryPause';
  tradingDay: string;

  // --- Pause obligatoire ---
  //
  // Le décompte tourne même macro désarmée ; seule l'application est conditionnée à `armed`. Voir
  // la pause arriver avant d'armer est ce qui la rend prévisible plutôt que subie.

  /** La pause est appliquée. Redondant avec `blockReason`, publié pour éviter au deck d'analyser une chaîne. */
  pauseActive: boolean;
  /** Secondes restantes sur la pause en cours. 0 s'il n'y en a pas. */
  pauseSecondsRemaining: number;
  /** Secondes de trading avant que la pause ne tombe. 0 si la règle est inactive, si rien n'a été tradé, ou si la pause est déjà due. */
  pauseDueInSeconds: number;

  // --- Liquidation automatique sur perte journalière ---
  //
  // La seule règle de Guard qui ENVOIE un ordre au lieu d'en refuser un. Le décompte est publié
  // pour qu'un trader qui voit « LIQUID 4s » puisse encore fermer à ses conditions — meilleure
  // issue que d'être liquidé par surprise, et cela ne coûte rien à la règle.

  /** Le compte est-il liquidé quand la perte journalière est atteinte ? */
  autoFlattenEnabled: boolean;
  /** Le seuil est franchi et la tolérance s'écoule. */
  autoFlattenPending: boolean;
  /** Secondes avant la liquidation. 0 si rien n'est en attente. */
  autoFlattenSecondsRemaining: number;
  /** Le compte a été liquidé aujourd'hui. La journée est terminée. */
  autoFlattenDone: boolean;
  /** La liquidation est partie mais n'a pas abouti : des positions peuvent être encore ouvertes. */
  autoFlattenFailed: boolean;

  // --- Anti-tilt ---
  //
  // Never blocks anything. The bridge detects, the host adds friction — a long deliberate press on
  // entry keys. Nothing here can lock the deck: `lockSecondsRemaining` above is Guard's business
  // and an anti-tilt episode never touches it.

  /** Whether the anti-tilt rules are allowed to add friction at all. */
  tiltEnabled: boolean;
  /** True when entry keys must be held before they fire. */
  tiltActive: boolean;
  /** Seconds left on the episode. 0 for the contextual conditions, which end with the situation. */
  tiltSecondsRemaining: number;
  tiltReason: '' | 'sizeEscalation' | 'giveBack' | 'consecutiveLosses' | 'averaging';
  /**
   * 'all' — an episode: it describes the trader, so every entry is slowed down.
   * 'increaseOnly' — a contextual condition: it describes the position, so only orders that would
   * make it BIGGER are slowed. Slowing down an order that reduces an oversized position would be
   * exactly backwards.
   */
  tiltScope: '' | 'all' | 'increaseOnly';
  /** How long an entry key must be held while the friction applies. */
  tiltHoldSeconds: number;
}

export const DEFAULT_SAFETY_STATUS: SafetyStatus = {
  armed: false,
  locked: false,
  lockSecondsRemaining: 0,
  lockDurationHours: 6,
  maxTradesWhenLosing: 15,
  dailyLossLimit: 300,
  maxContracts: 0,
  atContractCap: false,
  tradeCount: 0,
  sessionPnl: 0,
  pnlAvailable: false,
  entriesBlocked: false,
  blockReason: '',
  tradingDay: '',
  pauseActive: false,
  pauseSecondsRemaining: 0,
  pauseDueInSeconds: 0,
  autoFlattenEnabled: false,
  autoFlattenPending: false,
  autoFlattenSecondsRemaining: 0,
  autoFlattenDone: false,
  autoFlattenFailed: false,
  tiltEnabled: false,
  tiltActive: false,
  tiltSecondsRemaining: 0,
  tiltReason: '',
  tiltScope: '',
  tiltHoldSeconds: 20,
};

/**
 * L'état « rien n'est branché » : ni bridge, ni NinjaTrader, aucune position, aucun compte.
 *
 * Vit ici plutôt que dans `host.ts` parce qu'il a deux consommateurs. L'hôte s'en sert au démarrage,
 * avant la première publication du bridge. L'éditeur Bitlearn s'en sert en permanence : il ne
 * connaît pas l'état du marché, et dessiner l'aperçu d'une touche à partir d'un état inventé serait
 * pire qu'un repos honnête.
 *
 * C'est cet objet qui rend inutile toute ré-écriture manuelle des visuels au repos — la seule
 * chose dont l'éditeur a besoin, c'est de passer ceci à `computeVisual`.
 */
export const DISCONNECTED_STATE: TradingState = {
  account: '', instrument: '', quantity: 1, defaultQuantity: 1,
  ntConnected: false, pluginConnected: false, position: null, instrumentInfo: null,
  availableAccounts: [], cooldownEnabled: false, cooldownActive: false,
  cooldownSecondsRemaining: 0, cooldownSeconds: 60, safety: { ...DEFAULT_SAFETY_STATUS },
};

export interface PositionState {
  exists: boolean;
  direction: 'Long' | 'Short' | 'Flat';
  quantity: number;
  averagePrice: number;
  unrealizedPnl: number;
  hasStopOrder: boolean;
  /** Price of the stop that protects the position most tightly. */
  stopPrice: number;
  /** Number of working stops — greater than 1 on a scaled position. */
  stopOrderCount: number;
  hasTargetOrder: boolean;
  /** Price of the nearest target in the position's direction. */
  targetPrice: number;
  targetOrderCount: number;
  activeOrderCount: number;
}

/**
 * Payload of the `guardViolation` event: an order placed straight into NinjaTrader — SuperDOM,
 * Chart Trader, DOM — while the safety macro was refusing entries. The add-on saw it because it
 * runs inside the platform, and cancelled it before it could work.
 *
 * `cancelled: false` is the case worth reading: the order was seen but survived, almost always a
 * market order that filled before the platform reported it.
 */
export interface GuardViolation {
  violation: string;
  cancelled: boolean;
  orderId: string;
  orderAction: string;
  orderType: string;
  quantity: number;
  name: string;
  instrument: string;
  error?: string;
}

/** Payload of the `orderUpdate` event the add-on emits when NinjaTrader refuses an order. */
export interface OrderUpdate {
  orderId: string;
  orderState: string;
  rejected: boolean;
  error: string;
  reason: string;
  quantity: number;
  orderType?: string;
  orderAction?: string;
  name?: string;
  instrument?: string;
}

export interface InstrumentInfo {
  name: string;
  lastPrice: number;
  openPrice: number;
  settlementPrice: number;
  tickSize: number;
  pointValue: number;
}

export interface GlobalSettings {
  bridgeUrl: string;
  defaultAccount: string;
  defaultInstrument: string;
  defaultQuantity: number;
}

export const DEFAULT_GLOBAL_SETTINGS: GlobalSettings = {
  bridgeUrl: 'ws://127.0.0.1:8218',
  defaultAccount: '',
  defaultInstrument: '',
  defaultQuantity: 1,
};

export function createCommand(action: string, payload: Record<string, unknown> = {}): BridgeMessage {
  return {
    type: 'command',
    version: '1.0',
    requestId: crypto.randomUUID(),
    timestamp: new Date().toISOString(),
    source: 'plugin',
    action,
    payload,
  };
}
