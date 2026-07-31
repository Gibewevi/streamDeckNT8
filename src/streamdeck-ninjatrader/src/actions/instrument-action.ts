import { BaseAction } from './base-action.js';
import { TradingState, createCommand } from '../models/messages.js';
import { Colors } from '../utils/visuals.js';
import * as log from '../utils/logger.js';

export class InstrumentSelectAction extends BaseAction {
  async onKeyDown(context: string, settings: Record<string, unknown>): Promise<void> {
    // No hardcoded contract: an unconfigured key must do nothing rather than
    // switch the deck to some other instrument.
    const instrument = (settings.instrument as string) || '';
    if (!instrument) return;
    const cmd = createCommand('setInstrument', { instrument });
    log.info(`Switching instrument to ${instrument}`, cmd.requestId ?? undefined);

    const resp = await this.bridge.sendCommand(cmd);
    if (resp.result?.success) {
      this.flashSuccess(context);
    }
  }

  updateVisual(context: string, state: TradingState): void {
    const settings = this.getSettings(context);
    const instrument = (settings.instrument as string) || '';
    const displayLabel = (settings.displayLabel as string) || instrument.replace(/\s\d{2}-\d{2}$/, '');
    const isActive = state.instrument === instrument;

    this.setButtonVisual(context, {
      title: displayLabel || '???',
      subtitle: isActive ? 'ACTIVE' : '',
      bgColor: isActive ? Colors.instrumentActive : Colors.instrumentIndigo,
      textColor: isActive ? Colors.textGold : Colors.textWhite,
    });
  }
}
