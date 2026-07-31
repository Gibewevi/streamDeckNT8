/**
 * Structured logger for the Stream Deck plugin.
 *
 * Writes to two places:
 *   - the Stream Deck SDK logger (visible in Stream Deck's own plugin logs)
 *   - one file per day in %APPDATA%\StreamDeckTrader\logs\plugin-YYYY-MM-DD.log
 *
 * The file uses the same line format as the bridge and the NT8 add-on:
 *
 *   2026-07-31 14:23:45.123 | INFO  | plugin | KeyDown | message | key=value
 *
 * so the three files of a day can be sorted together to replay a whole session across the
 * plugin → bridge → NinjaTrader chain. Stream Deck's own logs are per-launch and get recycled,
 * which is exactly the wrong shape for "the button did nothing this morning at 9:32".
 */
export declare enum LogLevel {
    TRACE = 0,
    DEBUG = 1,
    INFO = 2,
    WARN = 3,
    ERROR = 4
}
/** Extra context for one event: a bare request id, or named fields. */
export type LogContext = string | Record<string, unknown> | undefined;
export declare function setLogLevel(level: LogLevel): void;
export declare function getLogFilePath(): string;
export declare function trace(msg: string, ctx?: LogContext): void;
export declare function debug(msg: string, ctx?: LogContext): void;
export declare function info(msg: string, ctx?: LogContext): void;
export declare function warn(msg: string, ctx?: LogContext): void;
export declare function error(msg: string, ctx?: LogContext): void;
/** A named event with its context — the form worth searching for in a post-mortem. */
export declare function event(category: string, msg: string, ctx?: LogContext): void;
/** Same as {@link event}, for a refusal or anything unexpected that is not a crash. */
export declare function eventWarn(category: string, msg: string, ctx?: LogContext): void;
/** Chatty per-tick detail (state pushes, visual refreshes). Below the default level. */
export declare function traceEvent(category: string, msg: string, ctx?: LogContext): void;
export declare function debugEvent(category: string, msg: string, ctx?: LogContext): void;
/** Logs a failure with its type, message and stack trace. */
export declare function fail(category: string, err: unknown, msg: string, ctx?: LogContext): void;
/**
 * Catches what would otherwise leave no trace at all: an exception escaping an event handler
 * kills the plugin process and Stream Deck silently restarts it, so without these handlers
 * the log simply stops mid-session with no reason recorded.
 */
export declare function installProcessHandlers(): void;
/** Opens today's file and records what is running, so a file never starts mid-flow. */
export declare function logSessionHeader(details: Record<string, unknown>): void;
