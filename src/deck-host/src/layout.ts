/**
 * Modèle de layout — remplace les profils `.sdProfile` d'Elgato.
 *
 * Un seul fichier JSON, lisible et versionnable, contenant les pages et l'affectation de chaque
 * emplacement. Il est rechargé à chaud : l'interface de configuration écrit, l'hôte redessine.
 * Aucun redémarrage, contrairement au manifeste Elgato qui n'était relu qu'au lancement.
 */
import { readFileSync, writeFileSync, existsSync, mkdirSync, copyFileSync } from 'fs';
import { dirname, join } from 'path';
import * as log from './logger.js';

/** Une touche affectée. `settings` est libre : le catalogue décrit les champs attendus. */
export interface SlotAssignment {
  actionId: string;
  settings: Record<string, unknown>;
}

export interface Page {
  name: string;
  /** Indexé par numéro d'emplacement (0 = haut-gauche), en ligne d'abord. */
  slots: Record<string, SlotAssignment>;
}

export interface Layout {
  version: 1;
  /** Modèle de deck ciblé, pour valider la taille de grille. */
  device: { columns: number; rows: number };
  brightness: number;
  pages: Page[];
}

export const DEFAULT_DATA_DIR = join(process.env.APPDATA || process.cwd(), 'StreamDeckTrader');
export const DEFAULT_LAYOUT_PATH = join(DEFAULT_DATA_DIR, 'layout.json');

/**
 * Layout de démarrage : transcription exacte du profil Elgato actif au 31/07/2026,
 * pour que l'hôte démarre sur la configuration déjà en service plutôt qu'un deck vide.
 * Les coordonnées Elgato « colonne,ligne » sont converties en index `ligne * colonnes + colonne`.
 */
export function seedLayout(columns = 5, rows = 3): Layout {
  const at = (col: number, row: number) => String(row * columns + col);
  const slots: Record<string, SlotAssignment> = {};
  const put = (col: number, row: number, actionId: string, settings: Record<string, unknown> = {}) => {
    slots[at(col, row)] = { actionId, settings };
  };

  put(0, 0, 'com.trader.ninjatrader.buymarket');
  put(1, 0, 'com.trader.ninjatrader.qtyplus');
  put(2, 0, 'com.trader.ninjatrader.buylimit');
  put(3, 0, 'com.trader.ninjatrader.targetplus');
  put(4, 0, 'com.trader.ninjatrader.targetminus');

  put(0, 1, 'com.trader.ninjatrader.sellmarket');
  put(1, 1, 'com.trader.ninjatrader.qtyminus');
  put(2, 1, 'com.trader.ninjatrader.selllimit');
  put(3, 1, 'com.trader.ninjatrader.beplus');
  put(4, 1, 'com.trader.ninjatrader.beminus');

  put(0, 2, 'com.trader.ninjatrader.breakeven', { offsetTicks: 8 });
  put(1, 2, 'com.trader.ninjatrader.cancelorders');
  put(2, 2, 'com.trader.ninjatrader.instrument', { instrument: 'MNQ', displayLabel: '' });
  put(3, 2, 'com.trader.ninjatrader.account');
  put(4, 2, 'com.trader.ninjatrader.cooldown');

  return {
    version: 1,
    device: { columns, rows },
    brightness: 80,
    pages: [{ name: 'Page 1', slots }],
  };
}

export class LayoutStore {
  #layout: Layout;
  #path: string;
  #listeners: ((l: Layout) => void)[] = [];

  constructor(path = DEFAULT_LAYOUT_PATH) {
    this.#path = path;
    this.#layout = this.#load();
  }

  get layout(): Layout {
    return this.#layout;
  }

  get path(): string {
    return this.#path;
  }

  #load(): Layout {
    if (!existsSync(this.#path)) {
      const seeded = seedLayout();
      this.#persist(seeded);
      log.event('Layout', 'Aucun layout trouvé — configuration initiale écrite', { path: this.#path });
      return seeded;
    }
    try {
      const parsed = JSON.parse(readFileSync(this.#path, 'utf8')) as Layout;
      if (!parsed.pages?.length) throw new Error('layout sans page');
      log.event('Layout', 'Layout chargé', { path: this.#path, pages: parsed.pages.length });
      return parsed;
    } catch (err) {
      // Un layout illisible ne doit pas empêcher le deck de fonctionner : on repart du modèle
      // de départ, mais on conserve le fichier fautif pour pouvoir le corriger.
      const backup = `${this.#path}.invalide-${Date.now()}`;
      try {
        copyFileSync(this.#path, backup);
      } catch {
        /* ignore */
      }
      log.fail('Layout', err, 'Layout illisible — retour à la configuration initiale', { backup });
      const seeded = seedLayout();
      this.#persist(seeded);
      return seeded;
    }
  }

  #persist(layout: Layout): void {
    mkdirSync(dirname(this.#path), { recursive: true });
    writeFileSync(this.#path, JSON.stringify(layout, null, 2), 'utf8');
  }

  /** Remplace le layout, l'écrit sur disque et notifie l'hôte pour un redessin immédiat. */
  update(layout: Layout): void {
    this.#layout = layout;
    this.#persist(layout);
    log.event('Layout', 'Layout mis à jour', { pages: layout.pages.length });
    for (const fn of this.#listeners) fn(layout);
  }

  onChange(fn: (l: Layout) => void): void {
    this.#listeners.push(fn);
  }
}
