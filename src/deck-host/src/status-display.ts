/**
 * Texte affiché par la touche « Status », selon son type.
 *
 * Repris tel quel de `src/streamdeck-ninjatrader/src/actions/status-action.ts`, débarrassé de
 * la classe `BaseAction` — qui n'était plus utilisée à l'exécution dans le plugin non plus.
 * Seule `getDisplayText` était réellement appelée.
 */
import { TradingState } from './messages.js';

export type StatusType = 'account' | 'instrument' | 'position' | 'pnl' | 'quantity' | 'connection' | 'safety';

/**
 * Abrège un nom de compte pour tenir sur une touche.
 *
 * `fallback` est le seul point sur lequel les appelants divergeaient : l'indicateur d'état écrit
 * « --- », la touche de sélection écrit « ACCT ». Les deux copies de cette fonction ne différaient
 * que par cette chaîne — et elles auraient fini par différer sur autre chose.
 */
export function formatAccountLabel(account: string, fallback: string): string {
  const value = account.trim();
  if (!value) return fallback;

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

export function getDisplayText(statusType: StatusType, state: TradingState | null): { title: string; subtitle: string } {
  if (!state) {
    return { title: '---', subtitle: 'No data' };
  }

  switch (statusType) {
    case 'account':
      return { title: formatAccountLabel(state.account || '', '---'), subtitle: 'Account' };

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

    case 'safety': {
      const safety = state.safety;
      if (!safety?.armed) return { title: 'OFF', subtitle: 'Safety' };
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
