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
import { Layout, Page, SlotAssignment, seedLayout } from './layout-model.js';

// Ré-exportés pour ne pas obliger les importeurs à connaître la scission.
export { seedLayout };
export type { Layout, Page, SlotAssignment };

export const DEFAULT_DATA_DIR = join(process.env.APPDATA || process.cwd(), 'StreamDeckTrader');
export const DEFAULT_LAYOUT_PATH = join(DEFAULT_DATA_DIR, 'layout.json');


/**
 * Valide un layout venant d'une source non sûre, et rend une copie normalisée.
 *
 * Appliquée AUX DEUX chemins d'entrée — fichier et WebSocket de l'interface. Seul le fichier
 * était contrôlé auparavant, alors que le réseau est la source la moins fiable des deux : un
 * `saveLayout` avec `pages: []` passait, et `host.navigate` calculait alors `(0 + 1) % 0`, soit
 * `NaN`, ce qui figeait le deck.
 *
 * ---
 * **Une seconde validation existe côté Bitlearn, et ce doublon est le dispositif — pas un défaut.**
 *
 * Voir `lib/tradeDeck/layout.js`. Elle est plus stricte de onze contrôles, parce qu'elle protège
 * d'autre chose : un document hostile venu d'Internet et destiné à une colonne `Json` de
 * PostgreSQL. Celle-ci protège d'un fichier local corrompu. Les deux s'appliquent **en série** —
 * ce qui arrive de Bitlearn repasse ici (`bitlearn.ts`), et c'est voulu.
 *
 * Ce fichier n'est donc PAS dans l'ensemble partagé de `scripts/emit-shared.mjs`. Seul
 * `layout-model.ts` (les types et la graine) l'est. Ne pas « unifier » par symétrie.
 */
export function validateLayout(value: unknown): Layout {
  const l = value as Partial<Layout> | null;
  if (!l || typeof l !== 'object') throw new Error('layout absent ou non-objet');
  if (!Array.isArray(l.pages) || l.pages.length === 0) throw new Error('layout sans page');

  const int = (v: unknown, fallback: number, min: number, max: number): number => {
    const n = Math.round(Number(v));
    return Number.isFinite(n) ? Math.min(max, Math.max(min, n)) : fallback;
  };

  const pages: Page[] = l.pages.map((p, i) => {
    const slots: Record<string, SlotAssignment> = {};
    for (const [key, raw] of Object.entries(p?.slots ?? {})) {
      // Un emplacement sans identifiant d'action n'est pas affichable : on l'ignore plutôt que de
      // laisser `computeVisual` recevoir `undefined` à chaque rendu.
      const a = raw as Partial<SlotAssignment> | null;
      if (!a || typeof a.actionId !== 'string' || !a.actionId) continue;
      if (!/^\d+$/.test(key)) continue;
      slots[key] = {
        actionId: a.actionId,
        settings: a.settings && typeof a.settings === 'object' ? a.settings : {},
      };
    }
    return { name: typeof p?.name === 'string' ? p.name : `Page ${i + 1}`, slots };
  });

  return {
    version: 1,
    device: {
      columns: int(l.device?.columns, 5, 1, 32),
      rows: int(l.device?.rows, 3, 1, 32),
    },
    brightness: int(l.brightness, 80, 0, 100),
    pages,
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
      const parsed = validateLayout(JSON.parse(readFileSync(this.#path, 'utf8')));
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

  /**
   * Remplace le layout, l'écrit sur disque et notifie l'hôte pour un redessin immédiat.
   *
   * Lève sur un layout invalide, sans rien écrire ni notifier : l'appelant (la WebSocket de
   * l'interface) journalise le refus. Mieux vaut conserver le layout en service qu'en persister
   * un qui laisserait le deck inutilisable.
   */
  update(layout: Layout): void {
    const clean = validateLayout(layout);
    this.#layout = clean;
    this.#persist(clean);
    log.event('Layout', 'Layout mis à jour', { pages: clean.pages.length });
    for (const fn of this.#listeners) fn(clean);
  }

  onChange(fn: (l: Layout) => void): void {
    this.#listeners.push(fn);
  }
}
