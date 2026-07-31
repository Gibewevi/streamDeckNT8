/**
 * Stream Deck NinjaTrader Trading Cockpit — Plugin Entry Point
 *
 * Wires the @elgato/streamdeck SDK to our bridge client and action handlers.
 */

import streamDeck from '@elgato/streamdeck';
import { spawn } from 'child_process';
import { existsSync } from 'fs';
import { join, dirname } from 'path';
import { fileURLToPath } from 'url';

// SingletonAction is exported but TS can't resolve it via Node16 module resolution
// Use dynamic import workaround
class NTAction {
  manifestId: string | undefined;
  onWillAppear?: (ev: any) => void;
  onWillDisappear?: (ev: any) => void;
  onDidReceiveSettings?: (ev: any) => void;
  onKeyDown?: (ev: any) => Promise<void> | void;
}
import { BridgeClient } from './services/bridge-client.js';
import {
  BridgeMessage,
  DEFAULT_GLOBAL_SETTINGS,
  DEFAULT_SAFETY_STATUS,
  GlobalSettings,
  OrderUpdate,
  SafetyStatus,
  TradingState,
  createCommand,
} from './models/messages.js';
import { renderButtonSvg, Colors } from './utils/visuals.js';
import * as log from './utils/logger.js';
import { StatusDisplayAction as StatusLogic, type StatusType } from './actions/status-action.js';

// Installed before anything else runs: a crash during startup is exactly the case where the
// log has to survive.
log.installProcessHandlers();

// --- Global State ---
const gs: GlobalSettings = { ...DEFAULT_GLOBAL_SETTINGS };
const bridge = new BridgeClient(gs.bridgeUrl);
let lastState: TradingState | null = null;

const DISCONNECTED_STATE: TradingState = {
  account: '',
  instrument: '',
  quantity: gs.defaultQuantity,
  defaultQuantity: gs.defaultQuantity,
  ntConnected: false,
  pluginConnected: false,
  position: null,
  instrumentInfo: null,
  availableAccounts: [],
  cooldownEnabled: false,
  cooldownActive: false,
  cooldownSecondsRemaining: 0,
  safety: { ...DEFAULT_SAFETY_STATUS },
};

// Safety macro settings last seen on a Safety key, re-pushed to the bridge on reconnect
let safetySettings: Record<string, unknown> = {};

// Last order rejection reported by NinjaTrader, shown briefly on the entry keys
const REJECTION_BANNER_MS = 5000;
let lastRejection: { reason: string; at: number } | null = null;

// Cooldown countdown timer — ticks locally every second for smooth display
let cooldownTimer: ReturnType<typeof setInterval> | null = null;

function startCooldownTimer() {
  if (cooldownTimer) return; // already running
  cooldownTimer = setInterval(() => {
    if (!lastState || !lastState.cooldownActive || lastState.cooldownSecondsRemaining <= 0) {
      stopCooldownTimer();
      return;
    }
    lastState.cooldownSecondsRemaining = Math.max(0, lastState.cooldownSecondsRemaining - 1);
    if (lastState.cooldownSecondsRemaining <= 0) {
      lastState.cooldownActive = false;
      stopCooldownTimer();
    }
    pushAllVisuals();
  }, 1000);
}

function stopCooldownTimer() {
  if (cooldownTimer) {
    clearInterval(cooldownTimer);
    cooldownTimer = null;
  }
}

// Track pending account change to prevent stateUpdate from overwriting it
let pendingAccountChange = 0;

// Track all visible action contexts for live updates
type TrackedAction = {
  sdAction: any; // KeyAction reference from SDK
  uuid: string;
  settings: Record<string, unknown>;
};
const tracked = new Map<string, TrackedAction>();

function normalizeAccountList(value: unknown): string[] {
  const rawItems = Array.isArray(value)
    ? value
    : typeof value === 'string'
      ? value.split(/[\n,;]/)
      : [];

  const seen = new Set<string>();
  const accounts: string[] = [];

  for (const item of rawItems) {
    const account = typeof item === 'string'
      ? item.trim()
      : typeof item === 'object' && item !== null && 'name' in item
        ? String((item as { name?: unknown }).name ?? '').trim()
        : '';

    if (!account) continue;

    const key = account.toUpperCase();
    if (seen.has(key)) continue;

    seen.add(key);
    accounts.push(account);
  }

  return accounts;
}

function formatAccountLabel(account: string): string {
  const value = account.trim();
  if (!value) return 'ACCT';

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

function getAccountCycleList(settings: Record<string, unknown>, state: TradingState | null): string[] {
  const availableAccounts = normalizeAccountList(state?.availableAccounts ?? []);
  if (availableAccounts.length === 0) return [];

  const configuredAccounts = normalizeAccountList(settings.accounts);
  if (configuredAccounts.length === 0) return availableAccounts;

  const activeByName = new Map(availableAccounts.map((account) => [account.toUpperCase(), account]));
  const configuredActiveAccounts = configuredAccounts
    .map((account) => activeByName.get(account.toUpperCase()))
    .filter((account): account is string => Boolean(account));

  return configuredActiveAccounts.length > 0 ? configuredActiveAccounts : availableAccounts;
}

/**
 * What to show on an entry key instead of its normal caption.
 * The safety macro wins over the cooldown because it is the rule the trader cannot lift.
 * `dimmed` distinguishes a real block (key does nothing) from a transient notice
 * (key still works) — a greyed-out key must never mean "your order was rejected".
 */
type EntryStatus = { label: string; dimmed: boolean } | null;

function entryStatus(state: TradingState): EntryStatus {
  if (state.safety?.entriesBlocked) {
    return {
      label: state.safety.blockReason === 'dailyLoss' ? 'LOSS LIMIT' : 'MAX TRADES',
      dimmed: true,
    };
  }
  if (state.cooldownActive) return { label: 'BLOCKED', dimmed: true };
  // NinjaTrader refused the last order: notify, but the key remains usable
  if (lastRejection && Date.now() - lastRejection.at < REJECTION_BANNER_MS) {
    return { label: 'REJECTED', dimmed: false };
  }
  return null;
}

function formatLockRemaining(seconds: number): string {
  if (seconds <= 0) return '--';
  if (seconds >= 3600) {
    const hours = Math.floor(seconds / 3600);
    const minutes = Math.floor((seconds % 3600) / 60);
    return `${hours}h${String(minutes).padStart(2, '0')}`;
  }
  if (seconds >= 60) return `${Math.floor(seconds / 60)}m`;
  return `${seconds}s`;
}

/** Bottom line of the Safety key: trades used and session P&L. */
function formatSafetyDetail(safety: SafetyStatus): string {
  const trades = safety.maxTradesWhenLosing > 0
    ? `${safety.tradeCount}/${safety.maxTradesWhenLosing}`
    : `${safety.tradeCount}T`;

  // Avoid '&' — this string is injected into SVG text.
  if (!safety.pnlAvailable) return `${trades} PNL?`;

  const pnl = Math.round(safety.sessionPnl);
  return `${trades} ${pnl >= 0 ? '+' : ''}${pnl}`;
}

// --- Visual update engine ---

// What each key currently shows. Visuals are recomputed on every state push (several times a
// second across a dozen keys); logging each one would bury the file, so only actual changes
// are recorded — which is also the only thing worth reading when a key "looks wrong".
const lastVisualSignature = new Map<string, string>();

function pushVisual(id: string) {
  const t = tracked.get(id);
  if (!t) return;
  const state = lastState ?? DISCONNECTED_STATE;
  const visual = computeVisual(t.uuid, t.settings, state);
  if (visual) {
    const svg = renderButtonSvg(visual);
    const detail = 'detail' in visual ? visual.detail : '';
    const signature = `${visual.title}|${visual.subtitle}|${detail}|${visual.bgColor}`;

    if (lastVisualSignature.get(id) !== signature) {
      lastVisualSignature.set(id, signature);
      log.debugEvent('Visual', `Key display changed: ${t.uuid}`, {
        title: visual.title,
        subtitle: visual.subtitle,
        bg: visual.bgColor,
        svgLen: svg.length,
      });
    } else {
      log.traceEvent('Visual', `Key refreshed unchanged: ${t.uuid}`, { title: visual.title });
    }

    t.sdAction.setImage(svg).catch((e: any) => log.fail('Visual', e, `setImage failed for ${t.uuid}`));
    // COUNTDOWN: use native title for the big number (SDK controls font size via setFeedbackLayout isn't available on Keypad)
    // All other actions: clear native title so only SVG text shows
    if (visual.title === 'COUNTDOWN') {
      t.sdAction.setTitle(`${visual.subtitle || ''}`).catch((e: any) => log.fail('Visual', e, `setTitle failed for ${t.uuid}`));
    } else {
      t.sdAction.setTitle('').catch((e: any) => log.fail('Visual', e, `setTitle failed for ${t.uuid}`));
    }
  }
}

function pushAllVisuals() {
  for (const id of tracked.keys()) {
    pushVisual(id);
  }
}

function computeVisual(uuid: string, settings: Record<string, unknown>, state: TradingState) {
  const connected = bridge.isConnected && state.ntConnected;

  const pos = state.position;
  const qty = state.quantity ?? gs.defaultQuantity;
  const defQty = state.defaultQuantity ?? gs.defaultQuantity;

  switch (uuid) {
    case 'com.trader.ninjatrader.buymarket': {
      const st = entryStatus(state);
      return {
        title: 'MKT', subtitle: st?.label ?? `Buy ×${qty}`,
        bgColor: st?.dimmed ? Colors.disabled : (connected ? Colors.buyGreen : Colors.buyGreenDim),
        textColor: st?.dimmed ? Colors.textDim : '#FFFFFF',
      };
    }
    case 'com.trader.ninjatrader.sellmarket': {
      const st = entryStatus(state);
      return {
        title: 'MKT', subtitle: st?.label ?? `Sell ×${qty}`,
        bgColor: st?.dimmed ? Colors.disabled : (connected ? Colors.sellRed : Colors.sellRedDim),
        textColor: st?.dimmed ? Colors.textDim : '#FFFFFF',
      };
    }
    case 'com.trader.ninjatrader.buylimit': {
      const st = entryStatus(state);
      return {
        title: 'LMT', subtitle: st?.label ?? `Buy ×${qty}`,
        bgColor: st?.dimmed ? Colors.disabled : (connected ? Colors.buyGreen : Colors.buyGreenDim),
        textColor: st?.dimmed ? Colors.textDim : '#FFFFFF',
      };
    }
    case 'com.trader.ninjatrader.selllimit': {
      const st = entryStatus(state);
      return {
        title: 'LMT', subtitle: st?.label ?? `Sell ×${qty}`,
        bgColor: st?.dimmed ? Colors.disabled : (connected ? Colors.sellRed : Colors.sellRedDim),
        textColor: st?.dimmed ? Colors.textDim : '#FFFFFF',
      };
    }
    case 'com.trader.ninjatrader.flatten':
      return {
        title: 'Close', subtitle: `Qty ${pos?.quantity ?? 0}`,
        bgColor: '#FFFFFF', textColor: '#000000',
      };
    case 'com.trader.ninjatrader.cancelorders': {
      const posQty = Math.abs(pos?.quantity ?? 0);
      return {
        title: 'QTY_CANCEL', subtitle: posQty > 0 ? `${posQty}` : '0',
        bgColor: posQty > 0 ? Colors.sellRed : '#FFFFFF',
        textColor: posQty > 0 ? '#FFFFFF' : '#000000',
      };
    }
    case 'com.trader.ninjatrader.cancelworkingorders': {
      // Cancels working orders only — the position is left untouched
      const orders = pos?.activeOrderCount ?? 0;
      return {
        title: 'CANCEL', subtitle: orders > 0 ? `${orders} order${orders > 1 ? 's' : ''}` : 'none',
        bgColor: orders > 0 ? Colors.cancelYellow : Colors.cancelYellowDim,
        textColor: orders > 0 ? '#000000' : Colors.textDim,
      };
    }
    case 'com.trader.ninjatrader.reverse': {
      // Reverse opens a position in the other direction, so the safety macro gates it too
      const st = entryStatus(state);
      return {
        title: 'Invert', subtitle: st?.label ?? `Qty ${pos?.quantity ?? 0}`,
        bgColor: st?.dimmed ? Colors.disabled : '#FFFFFF',
        textColor: st?.dimmed ? Colors.textDim : '#000000',
      };
    }
    case 'com.trader.ninjatrader.breakeven': {
      const offset = (settings.offsetTicks as number) ?? 0;
      return {
        title: 'BE',
        subtitle: `+${offset}`,
        bgColor: Colors.flattenOrange,
        textColor: Colors.textWhite,
      };
    }
    case 'com.trader.ninjatrader.stopplus':
      return {
        title: 'QTY_STOP_UP', subtitle: '1',
        bgColor: '#FFFFFF', textColor: '#000000',
      };
    case 'com.trader.ninjatrader.stopminus':
      return {
        title: 'QTY_STOP_DN', subtitle: '1',
        bgColor: '#FFFFFF', textColor: '#000000',
      };
    case 'com.trader.ninjatrader.targetplus':
      return {
        title: 'QTY_TARGET_UP', subtitle: '1',
        bgColor: '#FFFFFF', textColor: '#000000',
      };
    case 'com.trader.ninjatrader.targetminus':
      return {
        title: 'QTY_TARGET_DN', subtitle: '1',
        bgColor: '#FFFFFF', textColor: '#000000',
      };
    case 'com.trader.ninjatrader.beplus':
      return {
        title: 'QTY_BE_UP', subtitle: '1',
        bgColor: '#FFFFFF', textColor: '#000000',
      };
    case 'com.trader.ninjatrader.beminus':
      return {
        title: 'QTY_BE_DN', subtitle: '1',
        bgColor: '#FFFFFF', textColor: '#000000',
      };
    case 'com.trader.ninjatrader.qtyplus':
      return {
        title: 'QTY_PLUS', subtitle: `${qty}`,
        bgColor: '#FFFFFF', textColor: '#000000',
      };
    case 'com.trader.ninjatrader.qtyminus':
      return {
        title: 'QTY_MINUS', subtitle: `${qty}`,
        bgColor: '#FFFFFF', textColor: '#000000',
      };
    case 'com.trader.ninjatrader.qtyreset':
      return {
        title: 'QTY_RESET', subtitle: `${defQty}`,
        bgColor: '#FFFFFF', textColor: '#000000',
      };
    case 'com.trader.ninjatrader.instrument': {
      const cfgInstrument = (settings.instrument as string) || '';
      // If not configured, show a placeholder
      if (!cfgInstrument) {
        return {
          title: '---', subtitle: 'Config requis',
          bgColor: Colors.instrumentIndigo, textColor: Colors.textDim,
        };
      }
      const displayLabel = (settings.displayLabel as string) || cfgInstrument;
      // Match root symbol: "MNQ" matches "MNQ 06-25", or exact match
      const stateInst = state.instrument || '';
      const isActive = stateInst === cfgInstrument || stateInst.startsWith(cfgInstrument + ' ');
      // Calculate % change from settlement (previous close) or open
      let pctText = '';
      const info = state.instrumentInfo;
      if (isActive && info && info.lastPrice > 0) {
        const refPrice = info.settlementPrice > 0 ? info.settlementPrice : info.openPrice;
        if (refPrice > 0) {
          const pct = ((info.lastPrice - refPrice) / refPrice) * 100;
          const sign = pct >= 0 ? '+' : '';
          pctText = `${sign}${pct.toFixed(2)}%`;
        }
      }
      return {
        title: displayLabel,
        subtitle: isActive ? (pctText || 'ACTIVE') : 'INACTIVE',
        bgColor: isActive ? Colors.instrumentActive : Colors.disabled,
        textColor: isActive ? Colors.textGold : Colors.textDim,
      };
    }
    case 'com.trader.ninjatrader.account': {
      const currentAccount = state.ntConnected ? (state.account || '') : '';
      const isActive = connected && currentAccount !== '';
      return {
        title: formatAccountLabel(currentAccount),
        subtitle: isActive ? 'ACTIVE' : 'INACTIVE',
        bgColor: isActive ? Colors.instrumentActive : Colors.disabled,
        textColor: isActive ? Colors.textGold : Colors.textDim,
      };
    }
    case 'com.trader.ninjatrader.status': {
      const statusType = (settings.statusType as StatusType) || 'connection';
      const { title, subtitle } = StatusLogic.getDisplayText(statusType, state);
      let bgColor: string;
      let textColor = Colors.textWhite;
      switch (statusType) {
        case 'account': bgColor = Colors.statusDark; break;
        case 'instrument': bgColor = Colors.instrumentIndigo; break;
        case 'position': {
          const dir = pos?.direction;
          bgColor = dir === 'Long' ? Colors.buyGreen : dir === 'Short' ? Colors.sellRed : Colors.statusDark;
          break;
        }
        case 'pnl': {
          const pnl = pos?.unrealizedPnl ?? 0;
          bgColor = pnl > 0 ? Colors.buyGreen : pnl < 0 ? Colors.sellRed : Colors.statusDark;
          break;
        }
        case 'quantity': bgColor = Colors.qtySlate; break;
        case 'connection': bgColor = state.ntConnected ? Colors.buyGreen : Colors.sellRed; break;
        case 'safety': {
          const safety = state.safety;
          bgColor = !safety?.armed ? Colors.disabled : safety.entriesBlocked ? Colors.sellRed : Colors.buyGreen;
          break;
        }
        default: bgColor = Colors.statusDark;
      }
      return { title, subtitle, bgColor, textColor };
    }
    case 'com.trader.ninjatrader.cooldown': {
      const enabled = state.cooldownEnabled ?? false;
      const active = state.cooldownActive ?? false;
      const secs = state.cooldownSecondsRemaining ?? 0;
      if (active) {
        return { title: 'COUNTDOWN', subtitle: `${secs}`, bgColor: Colors.sellRed, textColor: '#FFFFFF' };
      }
      return {
        title: 'SEC', subtitle: enabled ? 'ON' : 'OFF',
        bgColor: enabled ? Colors.buyGreen : Colors.disabled,
        textColor: enabled ? '#FFFFFF' : Colors.textDim,
      };
    }
    case 'com.trader.ninjatrader.safety': {
      const safety = state.safety ?? DEFAULT_SAFETY_STATUS;

      if (!safety.armed) {
        return {
          title: 'SAFETY:GUARD', subtitle: 'OFF',
          detail: `${safety.maxTradesWhenLosing || '--'} / ${safety.dailyLossLimit || '--'}`,
          bgColor: Colors.disabled, textColor: Colors.textDim,
        };
      }

      // Armed and a limit is hit — entries are refused for the rest of the lock
      if (safety.entriesBlocked) {
        return {
          title: safety.blockReason === 'dailyLoss' ? 'SAFETY:LOSS' : 'SAFETY:MAX',
          subtitle: formatLockRemaining(safety.lockSecondsRemaining),
          detail: formatSafetyDetail(safety),
          bgColor: Colors.sellRed, textColor: Colors.textWhite,
        };
      }

      return {
        title: 'SAFETY:LOCK',
        subtitle: formatLockRemaining(safety.lockSecondsRemaining),
        detail: formatSafetyDetail(safety),
        bgColor: Colors.buyGreen, textColor: Colors.textWhite,
      };
    }
    default:
      return null;
  }
}

// --- SDK Action factory ---

function createSDAction(
  uuid: string,
  actionName: string,
  handler: (settings: Record<string, unknown>) => Promise<void>,
  onSettings?: (settings: Record<string, unknown>) => Promise<void>
) {
  const action = new NTAction();
  action.manifestId = uuid;

  const applySettings = (settings: Record<string, unknown>) => {
    if (!onSettings) return;
    onSettings(settings).catch((err: any) =>
      log.fail('Settings', err, `${actionName}: applying settings failed`, { settings }));
  };

  action.onWillAppear = (ev: any) => {
    const id = ev.action.id;
    const settings = ev.payload.settings as Record<string, unknown>;
    tracked.set(id, { sdAction: ev.action, uuid, settings });
    log.debugEvent('Key', `${actionName} appeared on the deck`, { id, uuid, settings });
    pushVisual(id);
    applySettings(settings);
  };

  action.onWillDisappear = (ev: any) => {
    tracked.delete(ev.action.id);
    lastVisualSignature.delete(ev.action.id);
    log.debugEvent('Key', `${actionName} removed from the deck`, { id: ev.action.id, uuid });
  };

  // Fired when the Property Inspector saves — used to push config to the bridge
  action.onDidReceiveSettings = (ev: any) => {
    const settings = ev.payload.settings as Record<string, unknown>;
    const t = tracked.get(ev.action.id);
    if (t) t.settings = settings;
    log.event('Settings', `${actionName}: settings changed`, { id: ev.action.id, uuid, settings });
    pushVisual(ev.action.id);
    applySettings(settings);
  };

  action.onKeyDown = async (ev: any) => {
    const settings = ev.payload.settings as Record<string, unknown>;
    // Update stored settings
    const t = tracked.get(ev.action.id);
    if (t) t.settings = settings;

    // Every press is recorded before anything is attempted: a key that produced no order at
    // all and a key whose order was refused look identical on the deck, and only this line
    // tells them apart afterwards.
    const startedAt = Date.now();
    log.event('KeyDown', `${actionName} pressed`, {
      uuid,
      qty: lastState?.quantity ?? gs.defaultQuantity,
      instrument: lastState?.instrument ?? '',
      account: lastState?.account ?? '',
      settings,
    });

    try {
      await handler(settings);
      ev.action.showOk();
      log.event('KeyDown', `${actionName} completed`, { uuid, elapsedMs: Date.now() - startedAt });
    } catch (err: any) {
      log.fail('KeyDown', err, `${actionName} failed`, { uuid, elapsedMs: Date.now() - startedAt });
      ev.action.showAlert();
    }
  };

  return action;
}

// --- Register all trading actions ---

async function sendCmd(action: string, payload: Record<string, unknown>, settings: Record<string, unknown>) {
  const selectedAccount = lastState?.ntConnected ? (lastState.account || '').trim() : '';
  const fallbackAccount = typeof settings.account === 'string' ? settings.account.trim() : '';
  const account = selectedAccount || fallbackAccount || gs.defaultAccount;
  const instrument = (settings.instrument as string) || lastState?.instrument || gs.defaultInstrument;
  const cmd = createCommand(action, { account, instrument, ...payload });

  log.event('Command', `Sending ${action}`, {
    req: cmd.requestId ?? '',
    account,
    instrument,
    payload,
  });

  const startedAt = Date.now();
  const resp = await bridge.sendCommand(cmd);
  const elapsedMs = Date.now() - startedAt;

  if (resp.error) {
    // Refusals are the normal way the bridge enforces safety, cooldown and NT8 availability:
    // they are expected events, not crashes, and the code is what identifies which rule fired.
    log.eventWarn('Command', `${action} refused`, {
      req: cmd.requestId ?? '',
      code: resp.error.code,
      reason: resp.error.message,
      elapsedMs,
    });
    throw new Error(`${resp.error.code}: ${resp.error.message}`);
  }

  log.event('Command', `${action} accepted`, {
    req: cmd.requestId ?? '',
    elapsedMs,
    result: resp.result,
  });
}

// Entry orders
streamDeck.actions.registerAction(createSDAction('com.trader.ninjatrader.buymarket', 'BuyMarket', async (s) => {
  await sendCmd('buyMarket', { quantity: lastState?.quantity ?? gs.defaultQuantity }, s);
}));
streamDeck.actions.registerAction(createSDAction('com.trader.ninjatrader.sellmarket', 'SellMarket', async (s) => {
  await sendCmd('sellMarket', { quantity: lastState?.quantity ?? gs.defaultQuantity }, s);
}));
streamDeck.actions.registerAction(createSDAction('com.trader.ninjatrader.buylimit', 'BuyLimit', async (s) => {
  await sendCmd('buyLimit', { quantity: lastState?.quantity ?? gs.defaultQuantity, offsetTicks: (s.offsetTicks as number) ?? -2 }, s);
}));
streamDeck.actions.registerAction(createSDAction('com.trader.ninjatrader.selllimit', 'SellLimit', async (s) => {
  await sendCmd('sellLimit', { quantity: lastState?.quantity ?? gs.defaultQuantity, offsetTicks: (s.offsetTicks as number) ?? 2 }, s);
}));

// Position management
streamDeck.actions.registerAction(createSDAction('com.trader.ninjatrader.flatten', 'Flatten', async (s) => {
  await sendCmd('flatten', {}, s);
}));
// Close All — cancels every pending order AND closes the position
streamDeck.actions.registerAction(createSDAction('com.trader.ninjatrader.cancelorders', 'CloseAll', async (s) => {
  await sendCmd('cancelOrders', {}, s);
}));
// Cancel Orders — cancels working orders only, position untouched
streamDeck.actions.registerAction(createSDAction('com.trader.ninjatrader.cancelworkingorders', 'CancelWorkingOrders', async (s) => {
  await sendCmd('cancelWorkingOrders', {}, s);
}));
streamDeck.actions.registerAction(createSDAction('com.trader.ninjatrader.reverse', 'Reverse', async (s) => {
  await sendCmd('reverse', {}, s);
}));
streamDeck.actions.registerAction(createSDAction('com.trader.ninjatrader.breakeven', 'BreakEven', async (s) => {
  await sendCmd('breakeven', { offsetTicks: (s.offsetTicks as number) ?? 0 }, s);
}));
streamDeck.actions.registerAction(createSDAction('com.trader.ninjatrader.stopplus', 'StopPlus', async (s) => {
  await sendCmd('moveStop', { deltaTicks: 1 }, s);
}));
streamDeck.actions.registerAction(createSDAction('com.trader.ninjatrader.stopminus', 'StopMinus', async (s) => {
  await sendCmd('moveStop', { deltaTicks: -1 }, s);
}));
streamDeck.actions.registerAction(createSDAction('com.trader.ninjatrader.targetplus', 'TargetPlus', async (s) => {
  await sendCmd('moveTarget', { deltaTicks: 1 }, s);
}));
streamDeck.actions.registerAction(createSDAction('com.trader.ninjatrader.targetminus', 'TargetMinus', async (s) => {
  await sendCmd('moveTarget', { deltaTicks: -1 }, s);
}));
streamDeck.actions.registerAction(createSDAction('com.trader.ninjatrader.beplus', 'BEPlus', async (s) => {
  await sendCmd('moveStop', { deltaTicks: 1 }, s);
}));
streamDeck.actions.registerAction(createSDAction('com.trader.ninjatrader.beminus', 'BEMinus', async (s) => {
  await sendCmd('moveStop', { deltaTicks: -1 }, s);
}));

// Quantity — update local state immediately from response for instant visual feedback
streamDeck.actions.registerAction(createSDAction('com.trader.ninjatrader.qtyplus', 'QtyPlus', async () => {
  const cmd = createCommand('qtyAdjust', { delta: 1 });
  const resp = await bridge.sendCommand(cmd);
  if (resp.result?.success && lastState) {
    lastState.quantity = (resp.result as any).quantity ?? lastState.quantity;
    pushAllVisuals();
  }
}));
streamDeck.actions.registerAction(createSDAction('com.trader.ninjatrader.qtyminus', 'QtyMinus', async () => {
  const cmd = createCommand('qtyAdjust', { delta: -1 });
  const resp = await bridge.sendCommand(cmd);
  if (resp.result?.success && lastState) {
    lastState.quantity = (resp.result as any).quantity ?? lastState.quantity;
    pushAllVisuals();
  }
}));
streamDeck.actions.registerAction(createSDAction('com.trader.ninjatrader.qtyreset', 'QtyReset', async () => {
  const cmd = createCommand('qtyReset', {});
  const resp = await bridge.sendCommand(cmd);
  if (resp.result?.success && lastState) {
    lastState.quantity = (resp.result as any).quantity ?? lastState.quantity;
    pushAllVisuals();
  }
}));

// Instrument — update local state immediately, send to bridge if connected
streamDeck.actions.registerAction(createSDAction('com.trader.ninjatrader.instrument', 'Instrument', async (s) => {
  const instrument = (s.instrument as string) || '';
  if (!instrument) return; // Not configured, skip

  // Always update local state for immediate visual feedback
  if (!lastState) lastState = { ...DISCONNECTED_STATE };
  lastState.instrument = instrument;
  pushAllVisuals();

  // Send to bridge if connected (fire-and-forget if disconnected)
  if (bridge.isConnected) {
    const cmd = createCommand('setInstrument', { instrument });
    await bridge.sendCommand(cmd);
  }
}));

// Account — cycle through active accounts published by NinjaTrader
streamDeck.actions.registerAction(createSDAction('com.trader.ninjatrader.account', 'Account', async (s) => {
  // Settings only reorder/filter accounts that are currently active in NT8.
  // They never reintroduce stale broker accounts that NT8 is not publishing.
  const accounts = getAccountCycleList(s, lastState);

  if (accounts.length === 0) {
    log.eventWarn('Account', 'Account key pressed but NinjaTrader publishes no active account', {
      ntConnected: lastState?.ntConnected ?? false,
      configured: s.accounts,
    });
    return;
  }

  const currentAccount = lastState?.ntConnected ? (lastState.account ?? '') : '';
  const currentIdx = accounts.indexOf(currentAccount);
  const nextIdx = (currentIdx + 1) % accounts.length;
  const nextAccount = accounts[nextIdx];
  log.event('Account', 'Cycling account', {
    from: currentAccount || '(none)',
    to: nextAccount,
    index: `${nextIdx + 1}/${accounts.length}`,
    available: accounts.join(','),
  });

  // Always update local state for immediate visual feedback
  if (!lastState) lastState = { ...DISCONNECTED_STATE };
  lastState.account = nextAccount;
  pendingAccountChange = Date.now();
  pushAllVisuals();

  // Send to bridge if connected
  if (bridge.isConnected) {
    const cmd = createCommand('setAccount', { account: nextAccount });
    await bridge.sendCommand(cmd);
  }
}));

// Status (display-only, press refreshes)
streamDeck.actions.registerAction(createSDAction('com.trader.ninjatrader.status', 'Status', async () => {
  const cmd = createCommand('getState', {});
  bridge.send(cmd);
}));

// Cooldown toggle
streamDeck.actions.registerAction(createSDAction('com.trader.ninjatrader.cooldown', 'Cooldown', async () => {
  const cmd = createCommand('toggleCooldown', {});
  await bridge.sendCommand(cmd);
}));

// --- Safety macro ---

/** Adopts the safety status the bridge attaches to every safety response. */
function applySafetyResponse(resp: BridgeMessage): void {
  const safety = resp.payload as unknown as SafetyStatus | undefined;
  if (!safety || typeof safety.armed !== 'boolean') return;
  if (!lastState) lastState = { ...DISCONNECTED_STATE };
  lastState.safety = { ...DEFAULT_SAFETY_STATUS, ...safety };
  pushAllVisuals();
}

/**
 * Sends the Property Inspector values to the bridge, which owns and persists them.
 * The bridge refuses the change while the macro is armed — that refusal is expected
 * and simply means the locked settings stay in force.
 */
async function pushSafetyConfig(settings: Record<string, unknown>): Promise<void> {
  const payload: Record<string, unknown> = {};
  for (const key of ['maxTradesWhenLosing', 'dailyLossLimit', 'lockDurationHours']) {
    const value = settings[key];
    if (typeof value === 'number' && Number.isFinite(value)) payload[key] = value;
  }

  safetySettings = settings;
  if (Object.keys(payload).length === 0 || !bridge.isConnected) return;

  const resp = await bridge.sendCommand(createCommand('configureSafety', payload));
  if (resp.error) {
    // Expected while the macro is armed — the locked limits stay in force.
    log.event('Safety', 'configureSafety refused by the bridge', {
      code: resp.error.code,
      reason: resp.error.message,
      requested: payload,
    });
  } else {
    log.event('Safety', 'Safety limits pushed to the bridge', { requested: payload });
  }
  applySafetyResponse(resp);
}

// Arms the macro, or attempts to disarm it once the lock has expired.
// A refusal from the bridge surfaces as showAlert on the key.
streamDeck.actions.registerAction(createSDAction('com.trader.ninjatrader.safety', 'Safety', async () => {
  const resp = await bridge.sendCommand(createCommand('toggleSafety', {}));
  applySafetyResponse(resp);
  if (resp.error) {
    log.eventWarn('Safety', 'toggleSafety refused (lock still running)', {
      code: resp.error.code,
      reason: resp.error.message,
    });
    throw new Error(`${resp.error.code}: ${resp.error.message}`);
  }
  log.event('Safety', 'Safety macro toggled', {
    armed: lastState?.safety?.armed ?? false,
    lockSecondsRemaining: lastState?.safety?.lockSecondsRemaining ?? 0,
  });
}, pushSafetyConfig));

// --- Bridge state listener ---
bridge.onStateUpdate((raw: any) => {
  // Parse raw bridge payload into TradingState
  // Bridge sends account as string, NT8 sends as {name, connected}
  const acctRaw = raw.account;
  const accountName = typeof acctRaw === 'string' ? acctRaw : (acctRaw?.name || '');
  const instRaw = raw.instrument;
  const instrumentName = typeof instRaw === 'string' ? instRaw : (instRaw?.name || '');
  const inst = typeof instRaw === 'object' ? (instRaw || {}) : {};
  const pos = raw.position || {};

  const state: TradingState = {
    account: accountName,
    instrument: instrumentName,
    quantity: raw.quantity ?? gs.defaultQuantity,
    defaultQuantity: raw.defaultQuantity ?? gs.defaultQuantity,
    ntConnected: raw.ntConnected ?? false,
    pluginConnected: true,
    position: pos.exists != null ? {
      exists: pos.exists,
      direction: pos.direction || 'Flat',
      quantity: pos.quantity || 0,
      averagePrice: pos.averagePrice || 0,
      unrealizedPnl: pos.unrealizedPnl || 0,
      hasStopOrder: pos.hasStopOrder || false,
      stopPrice: pos.stopPrice || 0,
      stopOrderCount: pos.stopOrderCount || 0,
      hasTargetOrder: pos.hasTargetOrder || false,
      targetPrice: pos.targetPrice || 0,
      targetOrderCount: pos.targetOrderCount || 0,
      activeOrderCount: pos.activeOrderCount || 0,
    } : null,
    instrumentInfo: inst.name ? {
      name: inst.name,
      lastPrice: inst.lastPrice || 0,
      openPrice: inst.openPrice || 0,
      settlementPrice: inst.settlementPrice || 0,
      tickSize: inst.tickSize || 0,
      pointValue: inst.pointValue || 0,
    } : null,
    availableAccounts: normalizeAccountList(raw.availableAccounts || []),
    cooldownEnabled: raw.cooldownEnabled ?? false,
    cooldownActive: raw.cooldownActive ?? false,
    cooldownSecondsRemaining: raw.cooldownSecondsRemaining ?? 0,
    // The bridge is the authority on the safety macro; never fabricate a permissive default
    safety: { ...DEFAULT_SAFETY_STATUS, ...(raw.safety ?? {}) },
  };

  // If a pending account change was recently sent, keep the local account instead of the bridge's stale value
  if (pendingAccountChange > 0 && Date.now() - pendingAccountChange < 3000 && lastState?.account) {
    state.account = lastState.account;
  } else {
    pendingAccountChange = 0;
  }

  // If local cooldown timer is already running, keep local countdown values to avoid jitter
  if (cooldownTimer && lastState?.cooldownActive) {
    state.cooldownActive = lastState.cooldownActive;
    state.cooldownSecondsRemaining = lastState.cooldownSecondsRemaining;
  }

  const previous = lastState;
  lastState = state;

  // Manage cooldown countdown timer
  if (state.cooldownActive && state.cooldownSecondsRemaining > 0) {
    startCooldownTimer();
  } else {
    stopCooldownTimer();
  }

  logStateTransitions(previous, state);
  log.traceEvent('State', 'State update applied', {
    account: state.account,
    ntConnected: state.ntConnected,
    instrument: state.instrument,
    quantity: state.quantity,
    accounts: state.availableAccounts.join(','),
    trackedKeys: tracked.size,
  });

  pushAllVisuals();
});

/**
 * Logs only what changed between two state pushes.
 *
 * The bridge republishes the full state every couple of seconds; logging each push would add
 * tens of thousands of identical lines a day and hide the handful that matter. Transitions —
 * NinjaTrader going away, the account switching, a position opening or closing, the safety
 * macro starting to refuse entries — are the timeline a diagnosis is actually built from.
 */
function logStateTransitions(prev: TradingState | null, next: TradingState): void {
  if (!prev) {
    log.event('State', 'First state received from the bridge', {
      account: next.account,
      instrument: next.instrument,
      ntConnected: next.ntConnected,
      accounts: next.availableAccounts.join(','),
    });
    return;
  }

  if (prev.ntConnected !== next.ntConnected) {
    const message = next.ntConnected
      ? 'NinjaTrader is connected'
      : 'NinjaTrader is NOT connected — trading keys will be refused';
    if (next.ntConnected) log.event('State', message);
    else log.eventWarn('State', message);
  }

  if (prev.account !== next.account) {
    log.event('State', 'Account changed', { from: prev.account || '(none)', to: next.account || '(none)' });
  }

  if (prev.instrument !== next.instrument) {
    log.event('State', 'Instrument changed', { from: prev.instrument || '(none)', to: next.instrument || '(none)' });
  }

  if (prev.quantity !== next.quantity) {
    log.event('State', 'Quantity changed', { from: prev.quantity, to: next.quantity });
  }

  const prevPos = prev.position;
  const nextPos = next.position;
  const prevSig = `${prevPos?.direction ?? 'none'}|${prevPos?.quantity ?? 0}|${prevPos?.averagePrice ?? 0}`;
  const nextSig = `${nextPos?.direction ?? 'none'}|${nextPos?.quantity ?? 0}|${nextPos?.averagePrice ?? 0}`;
  if (prevSig !== nextSig) {
    log.event('State', 'Position changed', {
      from: prevSig,
      direction: nextPos?.direction ?? 'none',
      quantity: nextPos?.quantity ?? 0,
      averagePrice: nextPos?.averagePrice ?? 0,
      unrealizedPnl: nextPos?.unrealizedPnl ?? 0,
    });
  }

  const prevSafety = prev.safety ?? DEFAULT_SAFETY_STATUS;
  const nextSafety = next.safety ?? DEFAULT_SAFETY_STATUS;
  if (prevSafety.armed !== nextSafety.armed) {
    log.event('Safety', nextSafety.armed ? 'Safety macro ARMED' : 'Safety macro disarmed', {
      maxTradesWhenLosing: nextSafety.maxTradesWhenLosing,
      dailyLossLimit: nextSafety.dailyLossLimit,
      lockSecondsRemaining: nextSafety.lockSecondsRemaining,
    });
  }
  if (prevSafety.entriesBlocked !== nextSafety.entriesBlocked) {
    if (nextSafety.entriesBlocked) {
      log.eventWarn('Safety', 'Entries are now BLOCKED by the safety macro', {
        reason: nextSafety.blockReason,
        tradeCount: nextSafety.tradeCount,
        sessionPnl: nextSafety.sessionPnl,
        lockSecondsRemaining: nextSafety.lockSecondsRemaining,
      });
    } else {
      log.event('Safety', 'Entries are allowed again');
    }
  }

  if (prev.cooldownEnabled !== next.cooldownEnabled) {
    log.event('Cooldown', `Cooldown ${next.cooldownEnabled ? 'enabled' : 'disabled'}`);
  }
  if (prev.cooldownActive !== next.cooldownActive) {
    log.event('Cooldown', next.cooldownActive ? 'Cooldown started — entries blocked' : 'Cooldown finished', {
      seconds: next.cooldownSecondsRemaining,
    });
  }

  const prevAccounts = prev.availableAccounts.join(',');
  const nextAccounts = next.availableAccounts.join(',');
  if (prevAccounts !== nextAccounts) {
    log.event('State', 'Available accounts changed', { from: prevAccounts || '(none)', to: nextAccounts || '(none)' });
  }
}

// --- Order rejections ---
// Account.Submit is asynchronous: a refused order (margin, closed market, invalid price)
// is reported later by NinjaTrader, long after the key already flashed OK. Surface it.
bridge.onMessage((msg) => {
  if (msg.type !== 'event' || msg.action !== 'orderUpdate') return;

  const update = (msg.payload ?? {}) as unknown as OrderUpdate;
  if (!update.rejected) return;

  const detail = [update.orderAction, update.orderType, update.instrument].filter(Boolean).join(' ');
  log.error(`ORDER REJECTED by NinjaTrader: ${detail || update.orderId}`, {
    orderId: update.orderId,
    reason: update.reason,
    error: update.error,
    orderAction: update.orderAction,
    orderType: update.orderType,
    instrument: update.instrument,
  });

  lastRejection = { reason: update.reason || update.error || 'Rejected', at: Date.now() };
  for (const t of tracked.values()) {
    t.sdAction.showAlert().catch(() => { /* key may have disappeared */ });
  }
  pushAllVisuals();

  // Clear the banner after a few seconds so the keys return to their normal display
  setTimeout(() => {
    if (lastRejection && Date.now() - lastRejection.at >= REJECTION_BANNER_MS) {
      lastRejection = null;
      pushAllVisuals();
    }
  }, REJECTION_BANNER_MS + 100);
});

bridge.onConnectionChange((connected) => {
  if (connected) log.event('Connection', 'Bridge CONNECTED', { url: gs.bridgeUrl });
  else log.eventWarn('Connection', 'Bridge DISCONNECTED — keys are inert until it comes back', { url: gs.bridgeUrl });
  pushAllVisuals();

  // Re-publish the configured limits so a bridge that started without a state file
  // picks them up. Ignored by the bridge while a lock is running.
  if (connected && Object.keys(safetySettings).length > 0) {
    pushSafetyConfig(safetySettings).catch((err: any) =>
      log.fail('Safety', err, 'Re-publishing safety limits after reconnect failed'));
  }
});

// --- Auto-launch bridge if not running ---
function launchBridge(): void {
  // Bridge exe is bundled alongside the plugin in ../bridge/
  const pluginDir = dirname(fileURLToPath(import.meta.url));
  const bridgePath = join(pluginDir, '..', 'bridge', 'StreamDeckBridge.exe');

  if (!existsSync(bridgePath)) {
    log.eventWarn('Bridge', 'Bridge executable not found — no bridge will be started', { path: bridgePath });
    return;
  }

  try {
    const child = spawn(bridgePath, [], {
      detached: true,
      stdio: 'ignore',
      windowsHide: true,
    });
    child.unref();
    // stdio is ignored, so this PID is the only handle on that process: its own log file
    // (bridge-YYYY-MM-DD.log, same folder) is where the rest of its story is written.
    log.event('Bridge', 'Bridge auto-launched', { pid: child.pid, path: bridgePath });
  } catch (err: any) {
    log.fail('Bridge', err, 'Failed to launch the bridge executable', { path: bridgePath });
  }
}

// --- Start ---
log.logSessionHeader({
  bridgeUrl: gs.bridgeUrl,
  defaultQuantity: gs.defaultQuantity,
  defaultAccount: gs.defaultAccount,
  defaultInstrument: gs.defaultInstrument,
});

launchBridge();

bridge.start();
streamDeck.connect();
