import { BaseAction } from './base-action.js';
import { TradingState } from '../models/messages.js';
export declare class BuyMarketAction extends BaseAction {
    onKeyDown(context: string, settings: Record<string, unknown>): Promise<void>;
    updateVisual(context: string, state: TradingState): void;
}
export declare class SellMarketAction extends BaseAction {
    onKeyDown(context: string, settings: Record<string, unknown>): Promise<void>;
    updateVisual(context: string, state: TradingState): void;
}
export declare class BuyLimitAction extends BaseAction {
    onKeyDown(context: string, settings: Record<string, unknown>): Promise<void>;
    updateVisual(context: string, state: TradingState): void;
}
export declare class SellLimitAction extends BaseAction {
    onKeyDown(context: string, settings: Record<string, unknown>): Promise<void>;
    updateVisual(context: string, state: TradingState): void;
}
