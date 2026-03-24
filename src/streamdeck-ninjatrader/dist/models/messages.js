/**
 * Message types matching the bridge protocol V1.
 */
export const DEFAULT_GLOBAL_SETTINGS = {
    bridgeUrl: 'ws://127.0.0.1:8218',
    defaultAccount: 'Sim101',
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