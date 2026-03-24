import { BaseAction } from './base-action.js';
import { TradingState } from '../models/messages.js';
export declare class QtyPresetAction extends BaseAction {
    onKeyDown(context: string, settings: Record<string, unknown>): Promise<void>;
    updateVisual(context: string, state: TradingState): void;
}
export declare class QtyAdjustAction extends BaseAction {
    onKeyDown(context: string, settings: Record<string, unknown>): Promise<void>;
    updateVisual(context: string, state: TradingState): void;
}
export declare class QtyResetAction extends BaseAction {
    onKeyDown(context: string, _settings: Record<string, unknown>): Promise<void>;
    updateVisual(context: string, state: TradingState): void;
}
