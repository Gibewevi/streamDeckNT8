import { BaseAction } from './base-action.js';
import { Colors } from '../utils/visuals.js';
export class BuyMarketAction extends BaseAction {
    async onKeyDown(context, settings) {
        const state = this.bridge.lastState;
        const qty = state?.quantity ?? this.globalSettings.defaultQuantity;
        await this.sendAction(context, 'buyMarket', { quantity: qty }, settings);
    }
    updateVisual(context, state) {
        const connected = this.bridge.isConnected && state.ntConnected;
        this.setButtonVisual(context, {
            title: 'BUY',
            subtitle: `MKT ×${state.quantity}`,
            bgColor: connected ? Colors.buyGreen : Colors.buyGreenDim,
            textColor: connected ? Colors.textWhite : Colors.textDim,
        });
    }
}
export class SellMarketAction extends BaseAction {
    async onKeyDown(context, settings) {
        const state = this.bridge.lastState;
        const qty = state?.quantity ?? this.globalSettings.defaultQuantity;
        await this.sendAction(context, 'sellMarket', { quantity: qty }, settings);
    }
    updateVisual(context, state) {
        const connected = this.bridge.isConnected && state.ntConnected;
        this.setButtonVisual(context, {
            title: 'SELL',
            subtitle: `MKT ×${state.quantity}`,
            bgColor: connected ? Colors.sellRed : Colors.sellRedDim,
            textColor: connected ? Colors.textWhite : Colors.textDim,
        });
    }
}
export class BuyLimitAction extends BaseAction {
    async onKeyDown(context, settings) {
        const state = this.bridge.lastState;
        const qty = state?.quantity ?? this.globalSettings.defaultQuantity;
        const offsetTicks = settings.offsetTicks ?? -2;
        await this.sendAction(context, 'buyLimit', { quantity: qty, offsetTicks }, settings);
    }
    updateVisual(context, state) {
        const connected = this.bridge.isConnected && state.ntConnected;
        const settings = this.getSettings(context);
        const offset = settings.offsetTicks ?? -2;
        this.setButtonVisual(context, {
            title: 'BUY',
            subtitle: `LMT ${offset}t`,
            bgColor: connected ? Colors.buyGreenDim : Colors.disabled,
            textColor: connected ? Colors.textWhite : Colors.textDim,
        });
    }
}
export class SellLimitAction extends BaseAction {
    async onKeyDown(context, settings) {
        const state = this.bridge.lastState;
        const qty = state?.quantity ?? this.globalSettings.defaultQuantity;
        const offsetTicks = settings.offsetTicks ?? 2;
        await this.sendAction(context, 'sellLimit', { quantity: qty, offsetTicks }, settings);
    }
    updateVisual(context, state) {
        const connected = this.bridge.isConnected && state.ntConnected;
        const settings = this.getSettings(context);
        const offset = settings.offsetTicks ?? 2;
        this.setButtonVisual(context, {
            title: 'SELL',
            subtitle: `LMT +${offset}t`,
            bgColor: connected ? Colors.sellRedDim : Colors.disabled,
            textColor: connected ? Colors.textWhite : Colors.textDim,
        });
    }
}
//# sourceMappingURL=order-actions.js.map