import { BaseAction } from './base-action.js';
import { TradingState } from '../models/messages.js';
import { Colors } from '../utils/visuals.js';

export class FlattenAction extends BaseAction {
  async onKeyDown(context: string, settings: Record<string, unknown>): Promise<void> {
    await this.sendAction(context, 'flatten', {}, settings);
  }

  updateVisual(context: string, state: TradingState): void {
    const hasPos = state.position?.exists ?? false;
    this.setButtonVisual(context, {
      title: 'FLAT',
      subtitle: hasPos ? `${state.position!.direction} ×${state.position!.quantity}` : 'No pos',
      bgColor: hasPos ? Colors.flattenOrange : Colors.flattenOrangeDim,
      textColor: hasPos ? Colors.textWhite : Colors.textDim,
    });
  }
}

export class CancelOrdersAction extends BaseAction {
  async onKeyDown(context: string, settings: Record<string, unknown>): Promise<void> {
    await this.sendAction(context, 'cancelOrders', {}, settings);
  }

  updateVisual(context: string, state: TradingState): void {
    const connected = this.bridge.isConnected && state.ntConnected;
    this.setButtonVisual(context, {
      title: 'CANCEL',
      subtitle: 'Orders',
      bgColor: connected ? Colors.cancelYellow : Colors.cancelYellowDim,
      textColor: connected ? '#000000' : Colors.textDim,
    });
  }
}

export class ReverseAction extends BaseAction {
  async onKeyDown(context: string, settings: Record<string, unknown>): Promise<void> {
    await this.sendAction(context, 'reverse', {}, settings);
  }

  updateVisual(context: string, state: TradingState): void {
    const hasPos = state.position?.exists ?? false;
    this.setButtonVisual(context, {
      title: 'REV',
      subtitle: hasPos ? `${state.position!.direction}→${state.position!.direction === 'Long' ? 'Short' : 'Long'}` : 'No pos',
      bgColor: hasPos ? Colors.reverseViolet : Colors.reverseVioletDim,
      textColor: hasPos ? Colors.textWhite : Colors.textDim,
    });
  }
}

export class BreakevenAction extends BaseAction {
  async onKeyDown(context: string, settings: Record<string, unknown>): Promise<void> {
    const offsetTicks = (settings.offsetTicks as number) ?? 0;
    await this.sendAction(context, 'breakeven', { offsetTicks }, settings);
  }

  updateVisual(context: string, state: TradingState): void {
    const settings = this.getSettings(context);
    const offset = (settings.offsetTicks as number) ?? 0;
    const hasPos = state.position?.exists ?? false;
    const hasStop = state.position?.hasStopOrder ?? false;
    const active = hasPos && hasStop;
    const label = offset > 0 ? `BE+${offset}` : 'BE';

    this.setButtonVisual(context, {
      title: label,
      subtitle: active ? `Stop→${state.position!.averagePrice}` : 'Inactive',
      bgColor: active ? Colors.beBlue : Colors.beBlueDim,
      textColor: active ? Colors.textWhite : Colors.textDim,
    });
  }
}

export class MoveStopAction extends BaseAction {
  async onKeyDown(context: string, settings: Record<string, unknown>): Promise<void> {
    const deltaTicks = (settings.deltaTicks as number) ?? 1;
    await this.sendAction(context, 'moveStop', { deltaTicks }, settings);
  }

  updateVisual(context: string, state: TradingState): void {
    const settings = this.getSettings(context);
    const delta = (settings.deltaTicks as number) ?? 1;
    const hasStop = state.position?.hasStopOrder ?? false;
    const sign = delta >= 0 ? '+' : '';
    const label = `S${sign}${delta}`;

    this.setButtonVisual(context, {
      title: label,
      subtitle: hasStop ? `@ ${state.position!.stopPrice}` : 'No stop',
      bgColor: hasStop ? Colors.stopAmber : Colors.stopAmberDim,
      textColor: hasStop ? Colors.textWhite : Colors.textDim,
    });
  }
}

export class MoveTargetAction extends BaseAction {
  async onKeyDown(context: string, settings: Record<string, unknown>): Promise<void> {
    const deltaTicks = (settings.deltaTicks as number) ?? 1;
    await this.sendAction(context, 'moveTarget', { deltaTicks }, settings);
  }

  updateVisual(context: string, state: TradingState): void {
    const settings = this.getSettings(context);
    const delta = (settings.deltaTicks as number) ?? 1;
    const hasTarget = state.position?.hasTargetOrder ?? false;
    const sign = delta >= 0 ? '+' : '';
    const label = `T${sign}${delta}`;

    this.setButtonVisual(context, {
      title: label,
      subtitle: hasTarget ? `@ ${state.position!.targetPrice}` : 'No target',
      bgColor: hasTarget ? Colors.targetTeal : Colors.targetTealDim,
      textColor: hasTarget ? Colors.textWhite : Colors.textDim,
    });
  }
}
