using System;

namespace TradeDeck.Atas.Strategy;

/// <summary>Routes a deck command to the engine method that performs it.</summary>
internal sealed class CommandDispatcher
{
    private readonly OrderEngine _engine;

    public CommandDispatcher(OrderEngine engine) => _engine = engine;

    /// <summary>True when this action is one of the thirteen the engine performs.</summary>
    public static bool Handles(string action) => action switch
    {
        "buyMarket" or "sellMarket" or "buyLimit" or "sellLimit" or "flatten" or "flattenAccount"
            or "cancelOrders" or "cancelWorkingOrders" or "reverse" or "breakeven"
            or "attachBracket" or "moveStop" or "moveTarget" => true,
        _ => false,
    };

    public BridgeMessage Dispatch(BridgeMessage cmd)
    {
        SdLog.Debug("[REQ:{0}] Dispatching: {1}", cmd.RequestId ?? "n/a", cmd.Action);

        try
        {
            return cmd.Action switch
            {
                "buyMarket" => _engine.BuyMarket(cmd),
                "sellMarket" => _engine.SellMarket(cmd),
                _ => BridgeMessage.Failure(cmd.RequestId, cmd.Action, "UNSUPPORTED_ACTION",
                         $"Action '{cmd.Action}' is not implemented yet by the ATAS strategy."),
            };
        }
        catch (Exception ex)
        {
            // Scoped to ONE command on purpose. An unexpected exception must cost that command, not
            // the session: the bridge would reconnect, the host would replay the same command, and
            // one malformed field would become a permanent loop.
            SdLog.Fail("Command", ex, "[REQ:{0}] Unhandled exception in {1}", cmd.RequestId ?? "n/a", cmd.Action);
            return BridgeMessage.Failure(cmd.RequestId, cmd.Action, "INTERNAL_ERROR", $"Unexpected error: {ex.Message}");
        }
    }
}
