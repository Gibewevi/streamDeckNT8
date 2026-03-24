import { BaseAction } from './base-action.js';
import { TradingState } from '../models/messages.js';
import { Colors } from '../utils/visuals.js';

export type StatusType = 'account' | 'instrument' | 'position' | 'pnl' | 'quantity' | 'connection';

/**
 * Generic status display action.
 * What it shows depends on the 'statusType' setting.
 */
export class StatusDisplayAction extends BaseAction {
  async onKeyDown(_context: string, settings: Record<string, unknown>): Promise<void> {
    // Status buttons are display-only; pressing refreshes the state
    const { createCommand } = await import('../models/messages.js');
    const cmd = createCommand('getState', {});
    this.bridge.send(cmd);
  }

  updateVisual(context: string, state: TradingState): void {
    const settings = this.getSettings(context);
    const statusType = (settings.statusType as StatusType) || 'connection';
    const { title, subtitle } = StatusDisplayAction.getDisplayText(statusType, state);

    let bgColor: string;
    let textColor = Colors.textWhite;

    switch (statusType) {
      case 'account':
        bgColor = Colors.statusDark;
        break;
      case 'instrument':
        bgColor = Colors.instrumentIndigo;
        break;
      case 'position': {
        const dir = state.position?.direction;
        bgColor = dir === 'Long' ? Colors.buyGreen : dir === 'Short' ? Colors.sellRed : Colors.statusDark;
        break;
      }
      case 'pnl': {
        const pnl = state.position?.unrealizedPnl ?? 0;
        bgColor = pnl > 0 ? Colors.buyGreen : pnl < 0 ? Colors.sellRed : Colors.statusDark;
        textColor = pnl >= 0 ? Colors.textWhite : Colors.textWhite;
        break;
      }
      case 'quantity':
        bgColor = Colors.qtySlate;
        break;
      case 'connection':
        bgColor = state.ntConnected ? Colors.buyGreen : Colors.sellRed;
        break;
      default:
        bgColor = Colors.statusDark;
    }

    this.setButtonVisual(context, { title, subtitle, bgColor, textColor });
  }

  /**
   * Returns the display text for a given status type and state.
   */
  static getDisplayText(statusType: StatusType, state: TradingState | null): { title: string; subtitle: string } {
    if (!state) {
      return { title: '---', subtitle: 'No data' };
    }

    switch (statusType) {
      case 'account':
        return { title: state.account || '---', subtitle: 'Account' };

      case 'instrument':
        return {
          title: state.instrument?.replace(/\s\d{2}-\d{2}$/, '') || '---',
          subtitle: state.instrument || '',
        };

      case 'position': {
        const pos = state.position;
        if (!pos?.exists) return { title: 'FLAT', subtitle: 'No position' };
        const dir = pos.direction === 'Long' ? '▲' : '▼';
        return { title: `${dir} ${pos.quantity}`, subtitle: `@ ${pos.averagePrice}` };
      }

      case 'pnl': {
        const pos = state.position;
        if (!pos?.exists) return { title: '$0', subtitle: 'P&L' };
        const pnl = pos.unrealizedPnl;
        const sign = pnl >= 0 ? '+' : '';
        return { title: `${sign}$${pnl.toFixed(0)}`, subtitle: 'P&L' };
      }

      case 'quantity':
        return { title: `${state.quantity ?? 1}`, subtitle: 'QTY' };

      case 'connection': {
        const nt = state.ntConnected ? '🟢' : '🔴';
        return { title: `NT ${nt}`, subtitle: state.ntConnected ? 'Connected' : 'Disconnected' };
      }

      default:
        return { title: '?', subtitle: '' };
    }
  }
}
