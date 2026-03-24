using System;
using NinjaTrader.NinjaScript.AddOns.StreamDeck.Models;
using NinjaTrader.NinjaScript.AddOns.StreamDeck.Utilities;

namespace NinjaTrader.NinjaScript.AddOns.StreamDeck.Services
{
    /// <summary>
    /// Dispatches incoming commands to the appropriate TradingEngine method.
    /// </summary>
    public class CommandDispatcher
    {
        private readonly TradingEngine _engine;

        public CommandDispatcher(TradingEngine engine)
        {
            _engine = engine;
        }

        public BridgeMessage Dispatch(BridgeMessage cmd)
        {
            SdLogger.Info("[REQ:{0}] Dispatching: {1}", cmd.RequestId, cmd.Action);

            try
            {
                switch (cmd.Action)
                {
                    case "buyMarket":
                        return _engine.BuyMarket(cmd);
                    case "sellMarket":
                        return _engine.SellMarket(cmd);
                    case "buyLimit":
                        return _engine.BuyLimit(cmd);
                    case "sellLimit":
                        return _engine.SellLimit(cmd);
                    case "flatten":
                        return _engine.Flatten(cmd);
                    case "cancelOrders":
                        return _engine.CancelOrders(cmd);
                    case "reverse":
                        return _engine.Reverse(cmd);
                    case "breakeven":
                        return _engine.BreakEven(cmd);
                    case "moveStop":
                        return _engine.MoveStop(cmd);
                    case "moveTarget":
                        return _engine.MoveTarget(cmd);
                    default:
                        SdLogger.Warn("[REQ:{0}] Unknown action: {1}", cmd.RequestId, cmd.Action);
                        return BridgeMessage.CreateError(cmd.RequestId, cmd.Action,
                            "UNSUPPORTED_ACTION", $"Action '{cmd.Action}' is not supported by the add-on.");
                }
            }
            catch (Exception ex)
            {
                SdLogger.Error(ex, $"[REQ:{cmd.RequestId}] Unhandled exception in {cmd.Action}");
                return BridgeMessage.CreateError(cmd.RequestId, cmd.Action,
                    "INTERNAL_ERROR", $"Unexpected error: {ex.Message}");
            }
        }
    }
}
