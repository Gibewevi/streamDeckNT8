/**
 * Structured logger for the Stream Deck plugin.
 * All messages prefixed with [NTDeck] for easy filtering.
 */
export declare enum LogLevel {
    DEBUG = 0,
    INFO = 1,
    WARN = 2,
    ERROR = 3
}
export declare function setLogLevel(level: LogLevel): void;
export declare function debug(msg: string, reqId?: string): void;
export declare function info(msg: string, reqId?: string): void;
export declare function warn(msg: string, reqId?: string): void;
export declare function error(msg: string, reqId?: string): void;
