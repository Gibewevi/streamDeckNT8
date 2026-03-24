import { BaseAction } from './base-action.js';
import { TradingState } from '../models/messages.js';
export type StatusType = 'account' | 'instrument' | 'position' | 'pnl' | 'quantity' | 'connection';
/**
 * Generic status display action.
 * What it shows depends on the 'statusType' setting.
 */
export declare class StatusDisplayAction extends BaseAction {
    onKeyDown(_context: string, settings: Record<string, unknown>): Promise<void>;
    updateVisual(context: string, state: TradingState): void;
    /**
     * Returns the display text for a given status type and state.
     */
    static getDisplayText(statusType: StatusType, state: TradingState | null): {
        title: string;
        subtitle: string;
    };
}
