/**
 * Journalisation de l'hôte.
 *
 * Même format de ligne, même dossier et même rotation que les trois composants existants
 * (voir `docs/logging-strategy.md`) : les fichiers d'une même journée restent triables ensemble
 * pour rejouer une session de bout en bout.
 *
 * Différence avec le plugin : plus de miroir vers `streamDeck.logger`, l'application Elgato
 * n'étant plus dans le circuit. Le fichier est désormais la seule trace — d'où
 * `installProcessHandlers`, sans lequel un crash ne laisserait rien.
 */
import { appendFileSync, mkdirSync, existsSync, readdirSync, statSync, unlinkSync } from 'fs';
import { join } from 'path';

const LOG_DIR = join(process.env.APPDATA || process.cwd(), 'StreamDeckTrader', 'logs');
const COMPONENT = 'host';
const MAX_BYTES = 25 * 1024 * 1024;
const RETENTION_DAYS = 30;

export type Level = 'TRACE' | 'DEBUG' | 'INFO' | 'WARN' | 'ERROR';

const LEVEL_ORDER: Record<Level, number> = { TRACE: 0, DEBUG: 1, INFO: 2, WARN: 3, ERROR: 4 };
let minLevel: Level = (process.env.DECKHOST_LOG_LEVEL as Level) || 'DEBUG';

let currentDay = '';
let currentFile = '';
let rollIndex = 0;

function today(): string {
  const d = new Date();
  return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`;
}

function stamp(): string {
  const d = new Date();
  const p = (n: number, w = 2) => String(n).padStart(w, '0');
  return `${today()} ${p(d.getHours())}:${p(d.getMinutes())}:${p(d.getSeconds())}.${p(d.getMilliseconds(), 3)}`;
}

/** Supprime les fichiers de plus de 30 jours, au passage de minuit. */
function purgeOldFiles(): void {
  try {
    const cutoff = Date.now() - RETENTION_DAYS * 86400_000;
    for (const name of readdirSync(LOG_DIR)) {
      if (!name.endsWith('.log')) continue;
      const full = join(LOG_DIR, name);
      if (statSync(full).mtimeMs < cutoff) unlinkSync(full);
    }
  } catch {
    // La purge est un confort : son échec ne doit jamais empêcher d'écrire un log.
  }
}

function resolveFile(): string {
  const day = today();
  if (day !== currentDay) {
    currentDay = day;
    rollIndex = 0;
    if (!existsSync(LOG_DIR)) mkdirSync(LOG_DIR, { recursive: true });
    purgeOldFiles();
  }

  let file = join(LOG_DIR, rollIndex === 0 ? `${COMPONENT}-${day}.log` : `${COMPONENT}-${day}.${rollIndex}.log`);
  // Un emballement ne doit pas remplir le disque avec un seul fichier illisible.
  try {
    while (existsSync(file) && statSync(file).size > MAX_BYTES) {
      rollIndex++;
      file = join(LOG_DIR, `${COMPONENT}-${day}.${rollIndex}.log`);
    }
  } catch {
    /* ignore */
  }
  currentFile = file;
  return file;
}

function write(level: Level, category: string, message: string, ctx?: Record<string, unknown>): void {
  if (LEVEL_ORDER[level] < LEVEL_ORDER[minLevel]) return;

  const parts = ctx
    ? Object.entries(ctx)
        .filter(([, v]) => v !== undefined && v !== null && v !== '')
        .map(([k, v]) => `${k}=${typeof v === 'object' ? JSON.stringify(v) : String(v)}`)
        .join(' ')
    : '';

  const line = `${stamp()} | ${level.padEnd(5)} | ${COMPONENT} | ${category} | ${message}${parts ? ' | ' + parts : ''}\n`;

  try {
    appendFileSync(resolveFile(), line, 'utf8');
  } catch {
    // Dernier recours : au moins la console.
    process.stderr.write(line);
  }
  if (level === 'ERROR' || level === 'WARN') process.stderr.write(line);
  else if (LEVEL_ORDER[level] >= LEVEL_ORDER.INFO) process.stdout.write(line);
}

export const event = (cat: string, msg: string, ctx?: Record<string, unknown>) => write('INFO', cat, msg, ctx);
export const eventWarn = (cat: string, msg: string, ctx?: Record<string, unknown>) => write('WARN', cat, msg, ctx);
export const debugEvent = (cat: string, msg: string, ctx?: Record<string, unknown>) => write('DEBUG', cat, msg, ctx);
/** Obligatoire dans toute boucle périodique : un INFO y produirait des centaines de milliers de lignes. */
export const traceEvent = (cat: string, msg: string, ctx?: Record<string, unknown>) => write('TRACE', cat, msg, ctx);

export function fail(cat: string, err: unknown, msg: string, ctx?: Record<string, unknown>): void {
  const e = err as Error;
  write('ERROR', cat, msg, { ...ctx, error: e?.message ?? String(err), stack: e?.stack?.split('\n')[1]?.trim() });
}

export function logSessionHeader(version: string): void {
  write('INFO', 'Session', '='.repeat(60));
  write('INFO', 'Session', `Hôte deck démarré`, { version, pid: process.pid, node: process.version, logFile: currentFile });
}

/**
 * Sans ces gestionnaires, une exception non interceptée tuerait le processus sans laisser
 * la moindre trace — et contrairement au plugin, plus aucune application tierce ne le relance.
 */
export function installProcessHandlers(): void {
  process.on('uncaughtException', (err) => {
    fail('Process', err, 'Exception non interceptée — arrêt de l\'hôte');
    process.exit(1);
  });
  process.on('unhandledRejection', (reason) => {
    fail('Process', reason, 'Promesse rejetée non interceptée');
  });
}

export function setLevel(level: Level): void {
  minLevel = level;
}
