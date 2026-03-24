import { BaseAction } from './base-action.js';
import { TradingState } from '../models/messages.js';
export declare class InstrumentSelectAction extends BaseAction {
    onKeyDown(context: string, settings: Record<string, unknown>): Promise<void>;
    updateVisual(context: string, state: TradingState): void;
}
