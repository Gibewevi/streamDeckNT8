/**
 * Stream Deck NinjaTrader Trading Cockpit — Plugin Entry Point
 *
 * Wires the @elgato/streamdeck SDK to our bridge client and action handlers.
 */
import streamDeck from '@elgato/streamdeck';
// SingletonAction is exported but TS can't resolve it via Node16 module resolution
// Use dynamic import workaround
class NTAction {
    manifestId;
    onWillAppear;
    onWillDisappear;
    onKeyDown;
}
import { BridgeClient } from './services/bridge-client.js';
import { DEFAULT_GLOBAL_SETTINGS, createCommand } from './models/messages.js';
import { renderButtonSvg, buildTitle, Colors } from './utils/visuals.js';
import { StatusDisplayAction as StatusLogic } from './actions/status-action.js';
// --- Global State ---
const gs = { ...DEFAULT_GLOBAL_SETTINGS };
const bridge = new BridgeClient(gs.bridgeUrl);
let lastState = null;
const DISCONNECTED_STATE = {
    account: '',
    instrument: '',
    quantity: gs.defaultQuantity,
    defaultQuantity: gs.defaultQuantity,
    ntConnected: false,
    pluginConnected: false,
    position: null,
    instrumentInfo: null,
    availableAccounts: [],
};
// Track pending account change to prevent stateUpdate from overwriting it
let pendingAccountChange = 0;
const tracked = new Map();
// --- Visual update engine ---
function pushVisual(id) {
    const t = tracked.get(id);
    if (!t)
        return;
    const state = lastState ?? DISCONNECTED_STATE;
    const visual = computeVisual(t.uuid, t.settings, state);
    if (visual) {
        const svg = renderButtonSvg(visual);
        const titleText = buildTitle(visual);
        streamDeck.logger.info(`pushVisual ${t.uuid}: title=${titleText.replace(/\n/g, '|')}, bg=${visual.bgColor}, svgLen=${svg.length}`);
        t.sdAction.setImage(svg).catch((e) => streamDeck.logger.error(`setImage error: ${e}`));
        t.sdAction.setTitle('').catch((e) => streamDeck.logger.error(`setTitle error: ${e}`));
    }
}
function pushAllVisuals() {
    for (const id of tracked.keys()) {
        pushVisual(id);
    }
}
function computeVisual(uuid, settings, state) {
    const connected = bridge.isConnected && state.ntConnected;
    const hasPos = state.position?.exists ?? false;
    const pos = state.position;
    const qty = state.quantity ?? gs.defaultQuantity;
    const defQty = state.defaultQuantity ?? gs.defaultQuantity;
    switch (uuid) {
        case 'com.trader.ninjatrader.buymarket':
            return {
                title: 'MKT', subtitle: `Buy ×${qty}`,
                bgColor: connected ? Colors.buyGreen : Colors.buyGreenDim,
                textColor: '#FFFFFF',
            };
        case 'com.trader.ninjatrader.sellmarket':
            return {
                title: 'MKT', subtitle: `Sell ×${qty}`,
                bgColor: connected ? Colors.sellRed : Colors.sellRedDim,
                textColor: '#FFFFFF',
            };
        case 'com.trader.ninjatrader.buylimit':
            return {
                title: 'LMT', subtitle: `Buy ×${qty}`,
                bgColor: connected ? Colors.buyGreen : Colors.buyGreenDim,
                textColor: '#FFFFFF',
            };
        case 'com.trader.ninjatrader.selllimit':
            return {
                title: 'LMT', subtitle: `Sell ×${qty}`,
                bgColor: connected ? Colors.sellRed : Colors.sellRedDim,
                textColor: '#FFFFFF',
            };
        case 'com.trader.ninjatrader.flatten':
            return {
                title: 'Close', subtitle: `Qty ${pos?.quantity ?? 0}`,
                bgColor: '#FFFFFF', textColor: '#000000',
            };
        case 'com.trader.ninjatrader.cancelorders': {
            const orderCount = pos?.activeOrderCount ?? 0;
            const posQty = Math.abs(pos?.quantity ?? 0);
            const total = orderCount + posQty;
            return {
                title: 'QTY_CANCEL', subtitle: total > 0 ? `${total}` : '0',
                bgColor: total > 0 ? Colors.sellRed : '#FFFFFF',
                textColor: total > 0 ? '#FFFFFF' : '#000000',
            };
        }
        case 'com.trader.ninjatrader.reverse':
            return {
                title: 'Invert', subtitle: `Qty ${pos?.quantity ?? 0}`,
                bgColor: '#FFFFFF', textColor: '#000000',
            };
        case 'com.trader.ninjatrader.breakeven': {
            const offset = settings.offsetTicks ?? 0;
            const hasStop = pos?.hasStopOrder ?? false;
            const active = hasPos && hasStop;
            const label = offset > 0 ? `BE+${offset}t` : 'BE';
            return {
                title: label,
                subtitle: active ? `Stop→${pos.averagePrice}` : 'Inactive',
                bgColor: active ? Colors.beBlue : Colors.beBlueDim,
                textColor: active ? Colors.textWhite : Colors.textDim,
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
            const cfgInstrument = settings.instrument || '';
            // If not configured, show a placeholder
            if (!cfgInstrument) {
                return {
                    title: '---', subtitle: 'Config requis',
                    bgColor: Colors.instrumentIndigo, textColor: Colors.textDim,
                };
            }
            const displayLabel = settings.displayLabel || cfgInstrument;
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
            const currentAccount = state.account || '';
            const isActive = connected && currentAccount !== '';
            const displayName = currentAccount.length > 6 ? currentAccount.substring(0, 6) : currentAccount;
            return {
                title: displayName || 'ACCT',
                subtitle: isActive ? 'ACTIVE' : 'INACTIVE',
                bgColor: isActive ? Colors.instrumentActive : Colors.disabled,
                textColor: isActive ? Colors.textGold : Colors.textDim,
            };
        }
        case 'com.trader.ninjatrader.status': {
            const statusType = settings.statusType || 'connection';
            const { title, subtitle } = StatusLogic.getDisplayText(statusType, state);
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
                    const dir = pos?.direction;
                    bgColor = dir === 'Long' ? Colors.buyGreen : dir === 'Short' ? Colors.sellRed : Colors.statusDark;
                    break;
                }
                case 'pnl': {
                    const pnl = pos?.unrealizedPnl ?? 0;
                    bgColor = pnl > 0 ? Colors.buyGreen : pnl < 0 ? Colors.sellRed : Colors.statusDark;
                    break;
                }
                case 'quantity':
                    bgColor = Colors.qtySlate;
                    break;
                case 'connection':
                    bgColor = state.ntConnected ? Colors.buyGreen : Colors.sellRed;
                    break;
                default: bgColor = Colors.statusDark;
            }
            return { title, subtitle, bgColor, textColor };
        }
        default:
            return null;
    }
}
// --- SDK Action factory ---
function createSDAction(uuid, actionName, handler) {
    const action = new NTAction();
    action.manifestId = uuid;
    action.onWillAppear = (ev) => {
        const id = ev.action.id;
        tracked.set(id, { sdAction: ev.action, uuid, settings: ev.payload.settings });
        pushVisual(id);
    };
    action.onWillDisappear = (ev) => {
        tracked.delete(ev.action.id);
    };
    action.onKeyDown = async (ev) => {
        const settings = ev.payload.settings;
        // Update stored settings
        const t = tracked.get(ev.action.id);
        if (t)
            t.settings = settings;
        try {
            await handler(settings);
            ev.action.showOk();
        }
        catch (err) {
            streamDeck.logger.error(`${actionName} error: ${err}`);
            ev.action.showAlert();
        }
    };
    return action;
}
// --- Register all trading actions ---
async function sendCmd(action, payload, settings) {
    const account = settings.account || lastState?.account || gs.defaultAccount;
    const instrument = settings.instrument || lastState?.instrument || gs.defaultInstrument;
    const cmd = createCommand(action, { account, instrument, ...payload });
    const resp = await bridge.sendCommand(cmd);
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
    await sendCmd('buyLimit', { quantity: lastState?.quantity ?? gs.defaultQuantity, offsetTicks: s.offsetTicks ?? -2 }, s);
}));
streamDeck.actions.registerAction(createSDAction('com.trader.ninjatrader.selllimit', 'SellLimit', async (s) => {
    await sendCmd('sellLimit', { quantity: lastState?.quantity ?? gs.defaultQuantity, offsetTicks: s.offsetTicks ?? 2 }, s);
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
    await sendCmd('breakeven', { offsetTicks: s.offsetTicks ?? 0 }, s);
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
        lastState.quantity = resp.result.quantity ?? lastState.quantity;
        pushAllVisuals();
    }
}));
streamDeck.actions.registerAction(createSDAction('com.trader.ninjatrader.qtyminus', 'QtyMinus', async () => {
    const cmd = createCommand('qtyAdjust', { delta: -1 });
    const resp = await bridge.sendCommand(cmd);
    if (resp.result?.success && lastState) {
        lastState.quantity = resp.result.quantity ?? lastState.quantity;
        pushAllVisuals();
    }
}));
streamDeck.actions.registerAction(createSDAction('com.trader.ninjatrader.qtyreset', 'QtyReset', async () => {
    const cmd = createCommand('qtyReset', {});
    const resp = await bridge.sendCommand(cmd);
    if (resp.result?.success && lastState) {
        lastState.quantity = resp.result.quantity ?? lastState.quantity;
        pushAllVisuals();
    }
}));
// Instrument — update local state immediately, send to bridge if connected
streamDeck.actions.registerAction(createSDAction('com.trader.ninjatrader.instrument', 'Instrument', async (s) => {
    const instrument = s.instrument || '';
    if (!instrument)
        return; // Not configured, skip
    // Always update local state for immediate visual feedback
    if (!lastState)
        lastState = { ...DISCONNECTED_STATE };
    lastState.instrument = instrument;
    pushAllVisuals();
    // Send to bridge if connected (fire-and-forget if disconnected)
    if (bridge.isConnected) {
        const cmd = createCommand('setInstrument', { instrument });
        await bridge.sendCommand(cmd);
    }
}));
// Account — cycle through available accounts (from bridge or settings fallback)
streamDeck.actions.registerAction(createSDAction('com.trader.ninjatrader.account', 'Account', async (s) => {
    // Settings accounts take priority (user picks exactly which accounts to cycle)
    // Fallback to bridge list filtered by prefix
    let accounts = [];
    if (s.accounts && s.accounts.trim().length > 0) {
        accounts = s.accounts.split(',').map(a => a.trim()).filter(a => a.length > 0);
    }
    else {
        accounts = (lastState?.availableAccounts ?? []).filter(a => a.startsWith('Sim') || a.startsWith('APEX-') || a.startsWith('PA-APEX-') || a.startsWith('BX'));
    }
    streamDeck.logger.info(`Account button pressed, accounts: ${accounts.length}`);
    if (accounts.length === 0) {
        streamDeck.logger.info('Account: no accounts available');
        return;
    }
    const currentAccount = lastState?.account ?? '';
    const currentIdx = accounts.indexOf(currentAccount);
    const nextIdx = (currentIdx + 1) % accounts.length;
    const nextAccount = accounts[nextIdx];
    streamDeck.logger.info(`Account cycle: current=${currentAccount}, idx=${currentIdx}, nextIdx=${nextIdx}, next=${nextAccount}, total=${accounts.length}`);
    // Always update local state for immediate visual feedback
    if (!lastState)
        lastState = { ...DISCONNECTED_STATE };
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
// --- Bridge state listener ---
bridge.onStateUpdate((raw) => {
    // Parse raw bridge payload into TradingState
    // Bridge sends account as string, NT8 sends as {name, connected}
    const acctRaw = raw.account;
    const accountName = typeof acctRaw === 'string' ? acctRaw : (acctRaw?.name || '');
    const instRaw = raw.instrument;
    const instrumentName = typeof instRaw === 'string' ? instRaw : (instRaw?.name || '');
    const inst = typeof instRaw === 'object' ? (instRaw || {}) : {};
    const pos = raw.position || {};
    const state = {
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
        availableAccounts: raw.availableAccounts || [],
    };
    // If a pending account change was recently sent, keep the local account instead of the bridge's stale value
    if (pendingAccountChange > 0 && Date.now() - pendingAccountChange < 3000 && lastState?.account) {
        state.account = lastState.account;
    }
    else {
        pendingAccountChange = 0;
    }
    lastState = state;
    streamDeck.logger.info(`StateUpdate received: account=${state.account}, ntConnected=${state.ntConnected}, instrument=${state.instrument}, accounts=[${state.availableAccounts.join(',')}], tracked=${tracked.size}`);
    pushAllVisuals();
});
bridge.onConnectionChange((connected) => {
    streamDeck.logger.info(`Bridge: ${connected ? 'CONNECTED' : 'DISCONNECTED'}`);
    pushAllVisuals();
});
// --- Start ---
bridge.start();
streamDeck.connect();
//# sourceMappingURL=plugin.js.map