/**
 * Structured logger for the Stream Deck plugin.
 * All messages prefixed with [NTDeck] for easy filtering.
 */

export enum LogLevel {
  DEBUG = 0,
  INFO = 1,
  WARN = 2,
  ERROR = 3,
}

let currentLevel = LogLevel.INFO;

export function setLogLevel(level: LogLevel): void {
  currentLevel = level;
}

function formatMsg(level: string, msg: string, reqId?: string): string {
  const ts = new Date().toISOString();
  const prefix = reqId ? `[NTDeck][${level}][REQ:${reqId}]` : `[NTDeck][${level}]`;
  return `${ts} ${prefix} ${msg}`;
}

export function debug(msg: string, reqId?: string): void {
  if (currentLevel <= LogLevel.DEBUG) console.debug(formatMsg('DEBUG', msg, reqId));
}

export function info(msg: string, reqId?: string): void {
  if (currentLevel <= LogLevel.INFO) console.log(formatMsg('INFO', msg, reqId));
}

export function warn(msg: string, reqId?: string): void {
  if (currentLevel <= LogLevel.WARN) console.warn(formatMsg('WARN', msg, reqId));
}

export function error(msg: string, reqId?: string): void {
  if (currentLevel <= LogLevel.ERROR) console.error(formatMsg('ERROR', msg, reqId));
}
