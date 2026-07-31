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
import streamDeck from '@elgato/streamdeck';
import { appendFileSync, createWriteStream, existsSync, mkdirSync, readdirSync, statSync, unlinkSync } from 'fs';
import { join } from 'path';
export var LogLevel;
(function (LogLevel) {
    LogLevel[LogLevel["TRACE"] = 0] = "TRACE";
    LogLevel[LogLevel["DEBUG"] = 1] = "DEBUG";
    LogLevel[LogLevel["INFO"] = 2] = "INFO";
    LogLevel[LogLevel["WARN"] = 3] = "WARN";
    LogLevel[LogLevel["ERROR"] = 4] = "ERROR";
})(LogLevel || (LogLevel = {}));
const NO_CATEGORY = '-';
const MAX_FILE_BYTES = 25 * 1024 * 1024;
/** U+FEFF, written once at the head of a new file. */
const UTF8_BOM = String.fromCharCode(0xfeff);
let currentLevel = LogLevel.DEBUG;
// --- File sink ------------------------------------------------------------------------
const logDirectory = resolveLogDirectory();
const retentionDays = resolveRetentionDays();
let stream = null;
let streamDate = '';
let streamPath = '';
let rollIndex = 0;
let fileSinkDisabled = false;
function resolveLogDirectory() {
    const override = process.env.STREAMDECK_TRADER_LOG_DIR;
    if (override && override.trim())
        return override.trim();
    const appData = process.env.APPDATA
        ?? (process.env.USERPROFILE ? join(process.env.USERPROFILE, 'AppData', 'Roaming') : '');
    return join(appData, 'StreamDeckTrader', 'logs');
}
function resolveRetentionDays() {
    const parsed = Number(process.env.STREAMDECK_TRADER_LOG_RETENTION_DAYS);
    return Number.isFinite(parsed) && parsed > 0 ? Math.floor(parsed) : 30;
}
function localDateStamp(d) {
    const pad = (n) => String(n).padStart(2, '0');
    return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}`;
}
function localTimestamp(d) {
    const pad = (n) => String(n).padStart(2, '0');
    const ms = String(d.getMilliseconds()).padStart(3, '0');
    return `${localDateStamp(d)} ${pad(d.getHours())}:${pad(d.getMinutes())}:${pad(d.getSeconds())}.${ms}`;
}
function ensureStream(now) {
    if (fileSinkDisabled)
        return null;
    const today = localDateStamp(now);
    if (stream && today !== streamDate) {
        closeStream();
        rollIndex = 0;
    }
    // A runaway loop must not fill the disk with one unopenable file: the day keeps its name
    // and continues in plugin-YYYY-MM-DD.1.log, .2.log…
    if (stream && stream.bytesWritten >= MAX_FILE_BYTES) {
        closeStream();
        rollIndex++;
    }
    if (stream)
        return stream;
    try {
        if (!existsSync(logDirectory))
            mkdirSync(logDirectory, { recursive: true });
        streamDate = today;
        streamPath = join(logDirectory, rollIndex === 0 ? `plugin-${today}.log` : `plugin-${today}.${rollIndex}.log`);
        // A BOM on a brand-new file only: the logs carry accents and em-dashes, and without it
        // Notepad and PowerShell 5.1 read them as ANSI and show mojibake.
        const isNewFile = !existsSync(streamPath);
        stream = createWriteStream(streamPath, { flags: 'a' });
        if (isNewFile)
            stream.write(UTF8_BOM);
        stream.on('error', () => {
            // Disk full, folder removed, permissions changed — keep the plugin running and fall
            // back to the Stream Deck logger alone.
            stream = null;
            fileSinkDisabled = true;
        });
        purgeOldFiles();
        return stream;
    }
    catch {
        fileSinkDisabled = true;
        return null;
    }
}
function closeStream() {
    try {
        stream?.end();
    }
    catch { /* already closed */ }
    stream = null;
}
function purgeOldFiles() {
    try {
        const cutoff = Date.now() - retentionDays * 24 * 60 * 60 * 1000;
        for (const name of readdirSync(logDirectory)) {
            if (!name.startsWith('plugin-') || !name.endsWith('.log'))
                continue;
            const path = join(logDirectory, name);
            if (path === streamPath)
                continue;
            if (statSync(path).mtimeMs >= cutoff)
                continue;
            try {
                unlinkSync(path);
            }
            catch { /* in use — retry tomorrow */ }
        }
    }
    catch {
        // Retention is best-effort; never block logging on it.
    }
}
/** Keeps one event on one line — embedded newlines break log parsing. */
function sanitize(value) {
    return value.includes('\n') || value.includes('\r')
        ? value.replace(/\r\n/g, ' / ').replace(/[\n\r]/g, '/')
        : value;
}
function formatContext(ctx) {
    if (ctx === undefined || ctx === null)
        return '';
    if (typeof ctx === 'string')
        return ctx ? ` | req=${ctx}` : '';
    const parts = [];
    for (const [key, value] of Object.entries(ctx)) {
        if (value === undefined)
            continue;
        const rendered = typeof value === 'object' && value !== null
            ? safeJson(value)
            : String(value);
        parts.push(`${key}=${sanitize(rendered)}`);
    }
    return parts.length > 0 ? ` | ${parts.join(' ')}` : '';
}
function safeJson(value) {
    try {
        return JSON.stringify(value) ?? String(value);
    }
    catch {
        return '(uncircularizable)';
    }
}
const LEVEL_LABELS = {
    [LogLevel.TRACE]: 'TRACE',
    [LogLevel.DEBUG]: 'DEBUG',
    [LogLevel.INFO]: 'INFO ',
    [LogLevel.WARN]: 'WARN ',
    [LogLevel.ERROR]: 'ERROR',
};
function write(level, category, msg, ctx) {
    if (level < currentLevel)
        return;
    const now = new Date();
    const context = formatContext(ctx);
    const line = `${localTimestamp(now)} | ${LEVEL_LABELS[level]} | plugin | ${category} | ${sanitize(msg)}${context}`;
    try {
        ensureStream(now)?.write(line + '\n');
    }
    catch {
        // Never let logging break a key press.
    }
    mirrorToStreamDeck(level, category, msg, context);
}
function mirrorToStreamDeck(level, category, msg, context) {
    try {
        const prefix = category === NO_CATEGORY ? '[NTDeck]' : `[NTDeck][${category}]`;
        const text = `${prefix} ${msg}${context}`;
        switch (level) {
            case LogLevel.TRACE:
                streamDeck.logger.trace(text);
                break;
            case LogLevel.DEBUG:
                streamDeck.logger.debug(text);
                break;
            case LogLevel.INFO:
                streamDeck.logger.info(text);
                break;
            case LogLevel.WARN:
                streamDeck.logger.warn(text);
                break;
            case LogLevel.ERROR:
                streamDeck.logger.error(text);
                break;
        }
    }
    catch {
        // The SDK logger is unavailable before connect() in some situations — the file already
        // has the line, which is the copy that matters.
    }
}
// --- Public API -----------------------------------------------------------------------
export function setLogLevel(level) {
    currentLevel = level;
}
export function getLogFilePath() {
    return fileSinkDisabled ? '' : streamPath;
}
export function trace(msg, ctx) {
    write(LogLevel.TRACE, NO_CATEGORY, msg, ctx);
}
export function debug(msg, ctx) {
    write(LogLevel.DEBUG, NO_CATEGORY, msg, ctx);
}
export function info(msg, ctx) {
    write(LogLevel.INFO, NO_CATEGORY, msg, ctx);
}
export function warn(msg, ctx) {
    write(LogLevel.WARN, NO_CATEGORY, msg, ctx);
}
export function error(msg, ctx) {
    write(LogLevel.ERROR, NO_CATEGORY, msg, ctx);
}
/** A named event with its context — the form worth searching for in a post-mortem. */
export function event(category, msg, ctx) {
    write(LogLevel.INFO, category, msg, ctx);
}
/** Same as {@link event}, for a refusal or anything unexpected that is not a crash. */
export function eventWarn(category, msg, ctx) {
    write(LogLevel.WARN, category, msg, ctx);
}
/** Chatty per-tick detail (state pushes, visual refreshes). Below the default level. */
export function traceEvent(category, msg, ctx) {
    write(LogLevel.TRACE, category, msg, ctx);
}
export function debugEvent(category, msg, ctx) {
    write(LogLevel.DEBUG, category, msg, ctx);
}
/** Logs a failure with its type, message and stack trace. */
export function fail(category, err, msg, ctx) {
    const e = err instanceof Error ? err : undefined;
    const detail = {
        ...(typeof ctx === 'object' && ctx !== null ? ctx : {}),
        ...(typeof ctx === 'string' ? { req: ctx } : {}),
        exception: e ? e.name : typeof err,
        message: e ? e.message : String(err),
    };
    write(LogLevel.ERROR, category, msg, detail);
    const stack = e?.stack;
    if (stack) {
        for (const stackLine of stack.split('\n').slice(1)) {
            const trimmed = stackLine.trim();
            if (trimmed)
                writeRaw(`    ${trimmed}`);
        }
    }
}
/** Appends a pre-formatted continuation line (stack frames) without a new header. */
function writeRaw(line) {
    try {
        ensureStream(new Date())?.write(line + '\n');
    }
    catch { /* ignore */ }
}
/**
 * Catches what would otherwise leave no trace at all: an exception escaping an event handler
 * kills the plugin process and Stream Deck silently restarts it, so without these handlers
 * the log simply stops mid-session with no reason recorded.
 */
export function installProcessHandlers() {
    process.on('uncaughtException', (err) => {
        fail('Process', err, 'Uncaught exception — plugin is going down');
        flushSync(`FATAL uncaughtException: ${err?.stack ?? err}`);
    });
    process.on('unhandledRejection', (reason) => {
        fail('Process', reason, 'Unhandled promise rejection');
    });
    process.on('exit', (code) => {
        write(LogLevel.INFO, 'Process', '=== Plugin process exiting ===', { exitCode: code });
    });
    for (const signal of ['SIGINT', 'SIGTERM']) {
        process.on(signal, () => {
            write(LogLevel.INFO, 'Process', 'Received signal — shutting down', { signal });
            process.exit(0);
        });
    }
}
/**
 * Last-resort synchronous append. The write stream is asynchronous, so on a fatal crash the
 * buffered lines would die with the process; this bypasses it.
 */
function flushSync(text) {
    if (fileSinkDisabled || !streamPath)
        return;
    try {
        appendFileSync(streamPath, `${localTimestamp(new Date())} | ERROR | plugin | Process | ${sanitize(text)}\n`);
    }
    catch {
        // Nothing left to try.
    }
}
/** Opens today's file and records what is running, so a file never starts mid-flow. */
export function logSessionHeader(details) {
    ensureStream(new Date());
    write(LogLevel.INFO, 'Session', '=== Stream Deck plugin session started ===', {
        pid: process.pid,
        node: process.version,
        logFile: getLogFilePath() || '(file sink unavailable)',
        ...details,
    });
}
//# sourceMappingURL=logger.js.map