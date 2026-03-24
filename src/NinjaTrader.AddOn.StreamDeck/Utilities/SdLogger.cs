using System;

namespace NinjaTrader.NinjaScript.AddOns.StreamDeck.Utilities
{
    /// <summary>
    /// Structured logger that writes to NinjaTrader's output window.
    /// Prefixes all messages with [StreamDeck] for easy filtering.
    /// </summary>
    public static class SdLogger
    {
        private const string Prefix = "[StreamDeck]";

        public static void Info(string message)
        {
            Log(string.Format("{0} INFO  | {1}", Prefix, message));
        }

        public static void Info(string format, params object[] args)
        {
            Info(string.Format(format, args));
        }

        public static void Warn(string message)
        {
            Log(string.Format("{0} WARN  | {1}", Prefix, message));
        }

        public static void Warn(string format, params object[] args)
        {
            Warn(string.Format(format, args));
        }

        public static void Error(string message)
        {
            Log(string.Format("{0} ERROR | {1}", Prefix, message));
        }

        public static void Error(string format, params object[] args)
        {
            Error(string.Format(format, args));
        }

        public static void Error(Exception ex, string message)
        {
            Error(string.Format("{0} — {1}: {2}", message, ex.GetType().Name, ex.Message));
        }

        public static void Debug(string message)
        {
            // Always log debug in this version for troubleshooting
            Log(string.Format("{0} DEBUG | {1}", Prefix, message));
        }

        public static void Debug(string format, params object[] args)
        {
            Debug(string.Format(format, args));
        }

        private static void Log(string message)
        {
            try
            {
                NinjaTrader.Code.Output.Process(message, PrintTo.OutputTab1);
            }
            catch
            {
                // Silently fail if NT output is not available
            }
        }
    }
}
