/**
 * Modèle de layout : les types, et la disposition de démarrage.
 *
 * Séparé de `layout.ts` parce que ce fichier fait partie de l'ensemble partagé avec Bitlearn, qui
 * a besoin de la même graine pour proposer un deck de départ dans son éditeur. Tout ce qui touche
 * au disque (`LayoutStore`, chemins) et la validation restent dans `layout.ts` : l'un dépend de
 * `fs`, l'autre porte un modèle de menace propre à chaque côté.
 */

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
