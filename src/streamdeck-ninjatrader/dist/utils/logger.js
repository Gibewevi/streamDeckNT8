/**
 * Structured logger for the Stream Deck plugin.
 * All messages prefixed with [NTDeck] for easy filtering.
 */
export var LogLevel;
(function (LogLevel) {
    LogLevel[LogLevel["DEBUG"] = 0] = "DEBUG";
    LogLevel[LogLevel["INFO"] = 1] = "INFO";
    LogLevel[LogLevel["WARN"] = 2] = "WARN";
    LogLevel[LogLevel["ERROR"] = 3] = "ERROR";
})(LogLevel || (LogLevel = {}));
let currentLevel = LogLevel.INFO;
export function setLogLevel(level) {
    currentLevel = level;
}
function formatMsg(level, msg, reqId) {
    const ts = new Date().toISOString();
    const prefix = reqId ? `[NTDeck][${level}][REQ:${reqId}]` : `[NTDeck][${level}]`;
    return `${ts} ${prefix} ${msg}`;
}
export function debug(msg, reqId) {
    if (currentLevel <= LogLevel.DEBUG)
        console.debug(formatMsg('DEBUG', msg, reqId));
}
export function info(msg, reqId) {
    if (currentLevel <= LogLevel.INFO)
        console.log(formatMsg('INFO', msg, reqId));
}
export function warn(msg, reqId) {
    if (currentLevel <= LogLevel.WARN)
        console.warn(formatMsg('WARN', msg, reqId));
}
export function error(msg, reqId) {
    if (currentLevel <= LogLevel.ERROR)
        console.error(formatMsg('ERROR', msg, reqId));
}
//# sourceMappingURL=logger.js.map