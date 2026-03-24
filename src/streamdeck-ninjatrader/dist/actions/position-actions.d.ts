import { BaseAction } from './base-action.js';
import { TradingState } from '../models/messages.js';
export declare class FlattenAction extends BaseAction {
    onKeyDown(context: string, settings: Record<string, unknown>): Promise<void>;
    updateVisual(context: string, state: TradingState): void;
}
export declare class CancelOrdersAction extends BaseAction {
    onKeyDown(context: string, settings: Record<string, unknown>): Promise<void>;
    updateVisual(context: string, state: TradingState): void;
}
export declare class ReverseAction extends BaseAction {
    onKeyDown(context: string, settings: Record<string, unknown>): Promise<void>;
    updateVisual(context: string, state: TradingState): void;
}
export declare class BreakevenAction extends BaseAction {
    onKeyDown(context: string, settings: Record<string, unknown>): Promise<void>;
    updateVisual(context: string, state: TradingState): void;
}
export declare class MoveStopAction extends BaseAction {
    onKeyDown(context: string, settings: Record<string, unknown>): Promise<void>;
    updateVisual(context: string, state: TradingState): void;
}
export declare class MoveTargetAction extends BaseAction {
    onKeyDown(context: string, settings: Record<string, unknown>): Promise<void>;
    updateVisual(context: string, state: TradingState): void;
}
