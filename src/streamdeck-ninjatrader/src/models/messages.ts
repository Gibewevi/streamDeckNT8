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
}

export interface PositionState {
  exists: boolean;
  direction: 'Long' | 'Short' | 'Flat';
  quantity: number;
  averagePrice: number;
  unrealizedPnl: number;
  hasStopOrder: boolean;
  stopPrice: number;
  hasTargetOrder: boolean;
  targetPrice: number;
  activeOrderCount: number;
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
  defaultAccount: 'Sim101',
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
