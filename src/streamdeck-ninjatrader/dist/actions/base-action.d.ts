import { BridgeClient } from '../services/bridge-client.js';
import { BridgeMessage, TradingState, GlobalSettings } from '../models/messages.js';
import { ButtonVisual } from '../utils/visuals.js';
/**
 * Base class for all Stream Deck actions.
 * Provides bridge communication, state tracking, and visual feedback.
 */
export declare abstract class BaseAction {
    protected bridge: BridgeClient;
    protected globalSettings: GlobalSettings;
    protected contexts: Set<string>;
    protected contextSettings: Map<string, Record<string, unknown>>;
    protected lastState: TradingState | null;
    constructor(bridge: BridgeClient, globalSettings: GlobalSettings);
    /**
     * Called when a key appears on the Stream Deck.
     */
    onWillAppear(context: string, settings: Record<string, unknown>): void;
    /**
     * Called when a key disappears.
     */
    onWillDisappear(context: string): void;
    /**
     * Called when the key is pressed.
     */
    abstract onKeyDown(context: string, settings: Record<string, unknown>): Promise<void>;
    /**
     * Update the visual appearance of the button based on state.
     */
    abstract updateVisual(context: string, state: TradingState): void;
    /**
     * Send a command and handle the response.
     */
    protected sendAction(context: string, action: string, payload?: Record<string, unknown>, settings?: Record<string, unknown>): Promise<BridgeMessage>;
    protected flashSuccess(context: string): void;
    protected flashError(context: string): void;
    protected setButtonVisual(context: string, visual: ButtonVisual): void;
    protected getSettings(context: string): Record<string, unknown>;
}
