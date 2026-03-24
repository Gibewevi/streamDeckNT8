import { getDisplayAdapter } from '../services/display-adapter.js';
import { createCommand } from '../models/messages.js';
import { renderButtonSvg } from '../utils/visuals.js';
import * as log from '../utils/logger.js';
/**
 * Base class for all Stream Deck actions.
 * Provides bridge communication, state tracking, and visual feedback.
 */
export class BaseAction {
    bridge;
    globalSettings;
    contexts = new Set();
    contextSettings = new Map();
    lastState = null;
    constructor(bridge, globalSettings) {
        this.bridge = bridge;
        this.globalSettings = globalSettings;
        bridge.onStateUpdate((state) => {
            this.lastState = state;
            for (const ctx of this.contexts) {
                this.updateVisual(ctx, state);
            }
        });
        bridge.onConnectionChange((connected) => {
            for (const ctx of this.contexts) {
                if (this.lastState)
                    this.updateVisual(ctx, this.lastState);
            }
        });
    }
    /**
     * Called when a key appears on the Stream Deck.
     */
    onWillAppear(context, settings) {
        this.contexts.add(context);
        this.contextSettings.set(context, settings);
        if (this.lastState) {
            this.updateVisual(context, this.lastState);
        }
    }
    /**
     * Called when a key disappears.
     */
    onWillDisappear(context) {
        this.contexts.delete(context);
        this.contextSettings.delete(context);
    }
    /**
     * Send a command and handle the response.
     */
    async sendAction(context, action, payload = {}, settings = {}) {
        const account = settings.account || this.globalSettings.defaultAccount;
        const instrument = settings.instrument || this.globalSettings.defaultInstrument;
        const cmd = createCommand(action, { account, instrument, ...payload });
        log.info(`Sending ${action}`, cmd.requestId ?? undefined);
        const resp = await this.bridge.sendCommand(cmd);
        if (resp.result?.success) {
            log.info(`${action} succeeded: ${resp.result.message || 'OK'}`, cmd.requestId ?? undefined);
            this.flashSuccess(context);
        }
        else {
            const errMsg = resp.error?.message || 'Unknown error';
            log.warn(`${action} failed: ${resp.error?.code} — ${errMsg}`, cmd.requestId ?? undefined);
            this.flashError(context);
        }
        return resp;
    }
    flashSuccess(context) {
        getDisplayAdapter().showOk(context);
    }
    flashError(context) {
        getDisplayAdapter().showAlert(context);
    }
    setButtonVisual(context, visual) {
        const adapter = getDisplayAdapter();
        adapter.setImage(context, renderButtonSvg(visual));
    }
    getSettings(context) {
        return this.contextSettings.get(context) ?? {};
    }
}
//# sourceMappingURL=base-action.js.map