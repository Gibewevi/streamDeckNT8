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
  onKeyDown?: (ev: any) => Promise<void> | void;
}
import { BridgeClient } from './services/bridge-client.js';
import { DEFAULT_GLOBAL_SETTINGS, GlobalSettings, TradingState, createCommand } from './models/messages.js';
import { renderButtonSvg, Colors } from './utils/visuals.js';
import { StatusDisplayAction as StatusLogic, type StatusType } from './actions/status-action.js';

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
};

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

// --- Visual update engine ---

function pushVisual(id: string) {
  const t = tracked.get(id);
  if (!t) return;
  const state = lastState ?? DISCONNECTED_STATE;
  const visual = computeVisual(t.uuid, t.settings, state);
  if (visual) {
    const svg = renderButtonSvg(visual);
    streamDeck.logger.info(`pushVisual ${t.uuid}: title=${visual.title}, bg=${visual.bgColor}, svgLen=${svg.length}`);
    t.sdAction.setImage(svg).catch((e: any) => streamDeck.logger.error(`setImage error: ${e}`));
    // COUNTDOWN: use native title for the big number (SDK controls font size via setFeedbackLayout isn't available on Keypad)
    // All other actions: clear native title so only SVG text shows
    if (visual.title === 'COUNTDOWN') {
      t.sdAction.setTitle(`${visual.subtitle || ''}`).catch((e: any) => streamDeck.logger.error(`setTitle error: ${e}`));
    } else {
      t.sdAction.setTitle('').catch((e: any) => streamDeck.logger.error(`setTitle error: ${e}`));
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
      const blocked = state.cooldownActive ?? false;
      return {
        title: 'MKT', subtitle: blocked ? 'BLOCKED' : `Buy ×${qty}`,
        bgColor: blocked ? Colors.disabled : (connected ? Colors.buyGreen : Colors.buyGreenDim),
        textColor: blocked ? Colors.textDim : '#FFFFFF',
      };
    }
    case 'com.trader.ninjatrader.sellmarket': {
      const blocked = state.cooldownActive ?? false;
      return {
        title: 'MKT', subtitle: blocked ? 'BLOCKED' : `Sell ×${qty}`,
        bgColor: blocked ? Colors.disabled : (connected ? Colors.sellRed : Colors.sellRedDim),
        textColor: blocked ? Colors.textDim : '#FFFFFF',
      };
    }
    case 'com.trader.ninjatrader.buylimit': {
      const blocked = state.cooldownActive ?? false;
      return {
        title: 'LMT', subtitle: blocked ? 'BLOCKED' : `Buy ×${qty}`,
        bgColor: blocked ? Colors.disabled : (connected ? Colors.buyGreen : Colors.buyGreenDim),
        textColor: blocked ? Colors.textDim : '#FFFFFF',
      };
    }
    case 'com.trader.ninjatrader.selllimit': {
      const blocked = state.cooldownActive ?? false;
      return {
        title: 'LMT', subtitle: blocked ? 'BLOCKED' : `Sell ×${qty}`,
        bgColor: blocked ? Colors.disabled : (connected ? Colors.sellRed : Colors.sellRedDim),
        textColor: blocked ? Colors.textDim : '#FFFFFF',
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
    case 'com.trader.ninjatrader.reverse':
      return {
        title: 'Invert', subtitle: `Qty ${pos?.quantity ?? 0}`,
        bgColor: '#FFFFFF', textColor: '#000000',
      };
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
    default:
      return null;
  }
}

// --- SDK Action factory ---

function createSDAction(uuid: string, actionName: string, handler: (settings: Record<string, unknown>) => Promise<void>) {
  const action = new NTAction();
  action.manifestId = uuid;

  action.onWillAppear = (ev: any) => {
    const id = ev.action.id;
    tracked.set(id, { sdAction: ev.action, uuid, settings: ev.payload.settings as Record<string, unknown> });
    pushVisual(id);
  };

  action.onWillDisappear = (ev: any) => {
    tracked.delete(ev.action.id);
  };

  action.onKeyDown = async (ev: any) => {
    const settings = ev.payload.settings as Record<string, unknown>;
    // Update stored settings
    const t = tracked.get(ev.action.id);
    if (t) t.settings = settings;

    try {
      await handler(settings);
      ev.action.showOk();
    } catch (err: any) {
      streamDeck.logger.error(`${actionName} error: ${err}`);
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
  streamDeck.logger.info(`sendCmd ${action}: account=${account}, instrument=${instrument}, payload=${JSON.stringify(payload)}`);
  const cmd = createCommand(action, { account, instrument, ...payload });
  const resp = await bridge.sendCommand(cmd);
  streamDeck.logger.info(`sendCmd ${action} response: ${JSON.stringify(resp)}`);
  if (resp.error) {
    streamDeck.logger.error(`${action} failed: ${resp.error.code} — ${resp.error.message}`);
    throw new Error(`${resp.error.code}: ${resp.error.message}`);
  }
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
streamDeck.actions.registerAction(createSDAction('com.trader.ninjatrader.cancelorders', 'CancelOrders', async (s) => {
  await sendCmd('cancelOrders', {}, s);
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
  streamDeck.logger.info(`Account button pressed, accounts: ${accounts.length}`);

  if (accounts.length === 0) {
    streamDeck.logger.info('Account: no accounts available');
    return;
  }

  const currentAccount = lastState?.ntConnected ? (lastState.account ?? '') : '';
  const currentIdx = accounts.indexOf(currentAccount);
  const nextIdx = (currentIdx + 1) % accounts.length;
  const nextAccount = accounts[nextIdx];
  streamDeck.logger.info(`Account cycle: current=${currentAccount}, idx=${currentIdx}, nextIdx=${nextIdx}, next=${nextAccount}, total=${accounts.length}`);

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
      hasTargetOrder: pos.hasTargetOrder || false,
      targetPrice: pos.targetPrice || 0,
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

  lastState = state;

  // Manage cooldown countdown timer
  if (state.cooldownActive && state.cooldownSecondsRemaining > 0) {
    startCooldownTimer();
  } else {
    stopCooldownTimer();
  }

  streamDeck.logger.info(`StateUpdate received: account=${state.account}, ntConnected=${state.ntConnected}, instrument=${state.instrument}, accounts=[${state.availableAccounts.join(',')}], tracked=${tracked.size}`);
  pushAllVisuals();
});

bridge.onConnectionChange((connected) => {
  streamDeck.logger.info(`Bridge: ${connected ? 'CONNECTED' : 'DISCONNECTED'}`);
  pushAllVisuals();
});

// --- Auto-launch bridge if not running ---
function launchBridge(): void {
  // Bridge exe is bundled alongside the plugin in ../bridge/
  const pluginDir = dirname(fileURLToPath(import.meta.url));
  const bridgePath = join(pluginDir, '..', 'bridge', 'StreamDeckBridge.exe');

  if (!existsSync(bridgePath)) {
    streamDeck.logger.warn(`Bridge exe not found at ${bridgePath}`);
    return;
  }

  try {
    const child = spawn(bridgePath, [], {
      detached: true,
      stdio: 'ignore',
      windowsHide: true,
    });
    child.unref();
    streamDeck.logger.info(`Bridge auto-launched (PID ${child.pid})`);
  } catch (err: any) {
    streamDeck.logger.error(`Failed to launch bridge: ${err.message}`);
  }
}

launchBridge();

// --- Start ---
bridge.start();
streamDeck.connect();
