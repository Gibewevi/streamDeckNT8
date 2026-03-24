import { BaseAction } from './base-action.js';
import { TradingState, createCommand } from '../models/messages.js';
import { Colors } from '../utils/visuals.js';
import * as log from '../utils/logger.js';

export class QtyPresetAction extends BaseAction {
  async onKeyDown(context: string, settings: Record<string, unknown>): Promise<void> {
    const qty = (settings.quantity as number) ?? 1;
    const cmd = createCommand('qtySet', { quantity: qty });
    log.info(`Setting qty to ${qty}`, cmd.requestId ?? undefined);

    const resp = await this.bridge.sendCommand(cmd);
    if (resp.result?.success) {
      this.flashSuccess(context);
    }
  }

  updateVisual(context: string, state: TradingState): void {
    const settings = this.getSettings(context);
    const preset = (settings.quantity as number) ?? 1;
    const isActive = state.quantity === preset;

    this.setButtonVisual(context, {
      title: `${preset}`,
      subtitle: 'QTY',
      bgColor: isActive ? Colors.qtyActive : Colors.qtySlate,
      textColor: isActive ? Colors.textWhite : Colors.textDim,
    });
  }
}

export class QtyAdjustAction extends BaseAction {
  async onKeyDown(context: string, settings: Record<string, unknown>): Promise<void> {
    const delta = (settings.delta as number) ?? 1;
    const cmd = createCommand('qtyAdjust', { delta });
    log.info(`Adjusting qty by ${delta}`, cmd.requestId ?? undefined);

    const resp = await this.bridge.sendCommand(cmd);
    if (resp.result?.success) {
      this.flashSuccess(context);
    }
  }

  updateVisual(context: string, state: TradingState): void {
    const settings = this.getSettings(context);
    const delta = (settings.delta as number) ?? 1;
    const sign = delta >= 0 ? '+' : '';

    this.setButtonVisual(context, {
      title: `${sign}${delta}`,
      subtitle: `QTY: ${state.quantity}`,
      bgColor: Colors.qtySlate,
      textColor: Colors.textWhite,
    });
  }
}

export class QtyResetAction extends BaseAction {
  async onKeyDown(context: string, _settings: Record<string, unknown>): Promise<void> {
    const cmd = createCommand('qtyReset', {});
    log.info('Resetting qty', cmd.requestId ?? undefined);

    const resp = await this.bridge.sendCommand(cmd);
    if (resp.result?.success) {
      this.flashSuccess(context);
    }
  }

  updateVisual(context: string, state: TradingState): void {
    this.setButtonVisual(context, {
      title: 'RST',
      subtitle: `→ ${state.defaultQuantity}`,
      bgColor: Colors.qtySlate,
      textColor: Colors.textGold,
    });
  }
}
