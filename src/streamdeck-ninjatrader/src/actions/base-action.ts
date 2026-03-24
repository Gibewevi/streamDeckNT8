import { BridgeClient } from '../services/bridge-client.js';
import { getDisplayAdapter } from '../services/display-adapter.js';
import { BridgeMessage, TradingState, GlobalSettings, createCommand } from '../models/messages.js';
import { renderButtonSvg, ButtonVisual, Colors } from '../utils/visuals.js';
import * as log from '../utils/logger.js';

/**
 * Base class for all Stream Deck actions.
 * Provides bridge communication, state tracking, and visual feedback.
 */
export abstract class BaseAction {
  protected bridge: BridgeClient;
  protected globalSettings: GlobalSettings;
  protected contexts = new Set<string>();
  protected contextSettings = new Map<string, Record<string, unknown>>();
  protected lastState: TradingState | null = null;

  constructor(bridge: BridgeClient, globalSettings: GlobalSettings) {
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
        if (this.lastState) this.updateVisual(ctx, this.lastState);
      }
    });
  }

  /**
   * Called when a key appears on the Stream Deck.
   */
  onWillAppear(context: string, settings: Record<string, unknown>): void {
    this.contexts.add(context);
    this.contextSettings.set(context, settings);
    if (this.lastState) {
      this.updateVisual(context, this.lastState);
    }
  }

  /**
   * Called when a key disappears.
   */
  onWillDisappear(context: string): void {
    this.contexts.delete(context);
    this.contextSettings.delete(context);
  }

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
  protected async sendAction(
    context: string,
    action: string,
    payload: Record<string, unknown> = {},
    settings: Record<string, unknown> = {}
  ): Promise<BridgeMessage> {
    const account = (settings.account as string) || this.globalSettings.defaultAccount;
    const instrument = (settings.instrument as string) || this.globalSettings.defaultInstrument;

    const cmd = createCommand(action, { account, instrument, ...payload });
    log.info(`Sending ${action}`, cmd.requestId ?? undefined);

    const resp = await this.bridge.sendCommand(cmd);

    if (resp.result?.success) {
      log.info(`${action} succeeded: ${resp.result.message || 'OK'}`, cmd.requestId ?? undefined);
      this.flashSuccess(context);
    } else {
      const errMsg = resp.error?.message || 'Unknown error';
      log.warn(`${action} failed: ${resp.error?.code} — ${errMsg}`, cmd.requestId ?? undefined);
      this.flashError(context);
    }

    return resp;
  }

  protected flashSuccess(context: string): void {
    getDisplayAdapter().showOk(context);
  }

  protected flashError(context: string): void {
    getDisplayAdapter().showAlert(context);
  }

  protected setButtonVisual(context: string, visual: ButtonVisual): void {
    const adapter = getDisplayAdapter();
    adapter.setImage(context, renderButtonSvg(visual));
  }

  protected getSettings(context: string): Record<string, unknown> {
    return this.contextSettings.get(context) ?? {};
  }
}
