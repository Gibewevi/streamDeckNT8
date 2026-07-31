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
    result?: {
        success: boolean;
        [key: string]: unknown;
    };
    error?: {
        code: string;
        message: string;
    };
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
    tradeCount: number;
    sessionPnl: number;
    /** False when NinjaTrader does not expose account P&L — the loss rules are then inert. */
    pnlAvailable: boolean;
    /** True when the bridge is currently refusing position-opening actions. */
    entriesBlocked: boolean;
    blockReason: '' | 'dailyLoss' | 'tradeLimit';
    tradingDay: string;
}
export declare const DEFAULT_SAFETY_STATUS: SafetyStatus;
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
export declare const DEFAULT_GLOBAL_SETTINGS: GlobalSettings;
export declare function createCommand(action: string, payload?: Record<string, unknown>): BridgeMessage;
