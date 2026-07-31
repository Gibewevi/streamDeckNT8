/**
 * Message types matching the bridge protocol V1.
 */
export const DEFAULT_SAFETY_STATUS = {
    armed: false,
    locked: false,
    lockSecondsRemaining: 0,
    lockDurationHours: 6,
    maxTradesWhenLosing: 15,
    dailyLossLimit: 300,
    tradeCount: 0,
    sessionPnl: 0,
    pnlAvailable: false,
    entriesBlocked: false,
    blockReason: '',
    tradingDay: '',
};
export const DEFAULT_GLOBAL_SETTINGS = {
    bridgeUrl: 'ws://127.0.0.1:8218',
    defaultAccount: '',
    defaultInstrument: '',
    defaultQuantity: 1,
};
export function createCommand(action, payload = {}) {
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
//# sourceMappingURL=messages.js.map