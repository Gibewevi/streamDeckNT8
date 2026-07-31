import { BaseAction } from './base-action.js';
import { Colors } from '../utils/visuals.js';
function formatAccountLabel(account) {
    const value = account.trim();
    if (!value)
        return '---';
    const compact = value.replace(/[^A-Za-z0-9]/g, '').toUpperCase();
    const letters = (value.match(/[A-Za-z]/g) ?? []).join('').toUpperCase();
    const digits = (value.match(/\d/g) ?? []).join('');
    const prefix = (letters.length >= 3 ? letters : compact || value.toUpperCase()).slice(0, 3);
    const suffix = digits.length >= 3
        ? digits.slice(-3)
        : compact.length > prefix.length
            ? compact.slice(-3)
            : '';
    return suffix ? `${prefix}-${suffix}` : prefix;
}
/**
 * Generic status display action.
 * What it shows depends on the 'statusType' setting.
 */
export class StatusDisplayAction extends BaseAction {
    async onKeyDown(_context, settings) {
        // Status buttons are display-only; pressing refreshes the state
        const { createCommand } = await import('../models/messages.js');
        const cmd = createCommand('getState', {});
        this.bridge.send(cmd);
    }
    updateVisual(context, state) {
        const settings = this.getSettings(context);
        const statusType = settings.statusType || 'connection';
        const { title, subtitle } = StatusDisplayAction.getDisplayText(statusType, state);
        let bgColor;
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
            case 'safety': {
                const safety = state.safety;
                bgColor = !safety?.armed ? Colors.disabled : safety.entriesBlocked ? Colors.sellRed : Colors.buyGreen;
                break;
            }
            default:
                bgColor = Colors.statusDark;
        }
        this.setButtonVisual(context, { title, subtitle, bgColor, textColor });
    }
    /**
     * Returns the display text for a given status type and state.
     */
    static getDisplayText(statusType, state) {
        if (!state) {
            return { title: '---', subtitle: 'No data' };
        }
        switch (statusType) {
            case 'account':
                return { title: formatAccountLabel(state.account || ''), subtitle: 'Account' };
            case 'instrument':
                return {
                    title: state.instrument?.replace(/\s\d{2}-\d{2}$/, '') || '---',
                    subtitle: state.instrument || '',
                };
            case 'position': {
                const pos = state.position;
                if (!pos?.exists)
                    return { title: 'FLAT', subtitle: 'No position' };
                const dir = pos.direction === 'Long' ? '▲' : '▼';
                return { title: `${dir} ${pos.quantity}`, subtitle: `@ ${pos.averagePrice}` };
            }
            case 'pnl': {
                const pos = state.position;
                if (!pos?.exists)
                    return { title: '$0', subtitle: 'P&L' };
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
            case 'safety': {
                const safety = state.safety;
                if (!safety?.armed)
                    return { title: 'OFF', subtitle: 'Safety' };
                const trades = safety.maxTradesWhenLosing > 0
                    ? `${safety.tradeCount}/${safety.maxTradesWhenLosing}`
                    : `${safety.tradeCount}`;
                // No '&' here: this string is injected as raw SVG text
                const pnl = safety.pnlAvailable ? `${Math.round(safety.sessionPnl)}` : '?';
                return { title: trades, subtitle: `PnL ${pnl}` };
            }
            default:
                return { title: '?', subtitle: '' };
        }
    }
}
//# sourceMappingURL=status-action.js.map