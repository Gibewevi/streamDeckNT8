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
  blockReason: '' | 'dailyLoss' | 'tradeLimit';
  tradingDay: string;

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
  tiltEnabled: false,
  tiltActive: false,
  tiltSecondsRemaining: 0,
  tiltReason: '',
  tiltScope: '',
  tiltHoldSeconds: 20,
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
