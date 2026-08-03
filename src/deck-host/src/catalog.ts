/**
 * Catalogue des actions disponibles.
 *
 * Remplace `manifest.json` : c'est la source unique qui alimente à la fois la palette de
 * l'interface de configuration et le moteur de visuels. Ajouter une action ici suffit — il n'y a
 * plus de manifeste relu au seul démarrage, ni de redémarrage d'application à faire.
 *
 * Les identifiants restent ceux du plugin Elgato (`com.trader.ninjatrader.*`) : le bridge et
 * l'add-on NT8 les connaissent déjà, et un layout importé depuis un profil Elgato reste lisible.
 */

/** Un champ de réglage, rendu automatiquement par l'interface. */
export interface SettingField {
  key: string;
  label: string;
  type: 'text' | 'number' | 'select' | 'textarea' | 'toggle';
  placeholder?: string;
  min?: number;
  max?: number;
  /**
   * Pas de saisie. `step: 1` marque un champ **entier** : l'interface arrondit la valeur avant
   * de l'enregistrer. Indispensable dès que le bridge relira le champ en entier — une décimale
   * y était refusée par une exception, pas par une validation.
   */
  step?: number;
  options?: { value: string; label: string }[];
  help?: string;
}

export interface ActionDef {
  /** Identifiant stable, stocké dans le layout. */
  id: string;
  /** Libellé dans la palette. */
  name: string;
  /** Regroupement dans la palette. */
  group: 'Ordres' | 'Position' | 'Quantité' | 'Stop / Target' | 'Sélection' | 'Affichage' | 'Navigation';
  /** Une ligne d'explication, affichée sous le libellé. */
  description: string;
  /** Réglages proposés quand la touche est sélectionnée. */
  settings?: SettingField[];
  /**
   * L'action entre ou sort d'une position : elle doit partir **immédiatement**.
   *
   * Aucune confirmation par appui long n'est proposée ni appliquée dessus. En séance rapide,
   * quelques centaines de millisecondes de retard pour sortir coûtent plus cher que ce que la
   * confirmation protège. La règle est appliquée par l'hôte, pas seulement masquée dans
   * l'interface : un réglage résiduel dans un layout ancien doit rester sans effet.
   */
  instant?: boolean;
}

const OFFSET_TICKS: SettingField = {
  key: 'offsetTicks',
  label: 'Décalage (ticks)',
  type: 'number',
  min: 0,
  max: 1000,
  step: 1,
  help: 'Distance en ticks appliquée à l\'ordre.',
};

const ORDER_SETTINGS: SettingField[] = [
  {
    key: 'instrument',
    label: 'Instrument (forçage)',
    type: 'text',
    placeholder: 'vide = instrument sélectionné',
    help: 'Laisser vide pour utiliser l\'instrument courant du cockpit.',
  },
  {
    key: 'account',
    label: 'Compte (repli)',
    type: 'text',
    placeholder: 'vide = compte sélectionné',
    help: 'Utilisé seulement si aucun compte n\'est sélectionné.',
  },
  OFFSET_TICKS,
];

export const CATALOG: ActionDef[] = [
  // --- Ordres ---
  // Les quatre entrées : instantanées, jamais de confirmation.
  { id: 'com.trader.ninjatrader.buymarket', name: 'Achat marché', group: 'Ordres', description: 'Ordre d\'achat au marché à la quantité courante.', settings: ORDER_SETTINGS, instant: true },
  { id: 'com.trader.ninjatrader.sellmarket', name: 'Vente marché', group: 'Ordres', description: 'Ordre de vente au marché à la quantité courante.', settings: ORDER_SETTINGS, instant: true },
  { id: 'com.trader.ninjatrader.buylimit', name: 'Achat limite', group: 'Ordres', description: 'Ordre d\'achat limite, décalé sous le marché.', settings: ORDER_SETTINGS, instant: true },
  { id: 'com.trader.ninjatrader.selllimit', name: 'Vente limite', group: 'Ordres', description: 'Ordre de vente limite, décalé au-dessus du marché.', settings: ORDER_SETTINGS, instant: true },

  // --- Position ---
  // Trois commandes proches mais distinctes. Les noms suivent ce que l'add-on renvoie
  // réellement, vérifié le 31/07/2026 contre NinjaTrader :
  //   flatten            → « Flatten MNQ 09-26 submitted »
  //   cancelOrders       → « Cancelled all orders and closed position »
  //   cancelWorkingOrders→ « Cancelled 1 order(s) », position intacte
  // Sorties et retournement : instantanés eux aussi. Retarder une sortie est le pire moment
  // pour ajouter une friction.
  { id: 'com.trader.ninjatrader.flatten', name: 'Flatten', group: 'Position', description: 'Ferme la position au marché. Affiche FLAT sur la touche.', instant: true },
  { id: 'com.trader.ninjatrader.cancelorders', name: 'Tout fermer', group: 'Position', description: 'Annule tous les ordres en attente ET ferme la position. Affiche CLOSE.', instant: true },
  { id: 'com.trader.ninjatrader.cancelworkingorders', name: 'Annuler ordres', group: 'Position', description: 'Annule les ordres en attente, la position reste ouverte.' },
  { id: 'com.trader.ninjatrader.reverse', name: 'Inverser', group: 'Position', description: 'Retourne la position dans le sens opposé.', instant: true },
  { id: 'com.trader.ninjatrader.breakeven', name: 'Break-even', group: 'Position', description: 'Déplace le stop au point mort, avec décalage.', settings: [OFFSET_TICKS] },

  // --- Stop / Target ---
  { id: 'com.trader.ninjatrader.stopplus', name: 'Stop +', group: 'Stop / Target', description: 'Éloigne le stop d\'un cran.' },
  { id: 'com.trader.ninjatrader.stopminus', name: 'Stop −', group: 'Stop / Target', description: 'Rapproche le stop d\'un cran.' },
  { id: 'com.trader.ninjatrader.targetplus', name: 'Target +', group: 'Stop / Target', description: 'Éloigne l\'objectif d\'un cran.' },
  { id: 'com.trader.ninjatrader.targetminus', name: 'Target −', group: 'Stop / Target', description: 'Rapproche l\'objectif d\'un cran.' },
  { id: 'com.trader.ninjatrader.beplus', name: 'BE +', group: 'Stop / Target', description: 'Augmente le décalage du break-even.' },
  { id: 'com.trader.ninjatrader.beminus', name: 'BE −', group: 'Stop / Target', description: 'Diminue le décalage du break-even.' },

  // --- Quantité ---
  { id: 'com.trader.ninjatrader.qtyplus', name: 'Quantité +', group: 'Quantité', description: 'Incrémente la quantité de travail.' },
  { id: 'com.trader.ninjatrader.qtyminus', name: 'Quantité −', group: 'Quantité', description: 'Décrémente la quantité de travail.' },
  { id: 'com.trader.ninjatrader.qtyreset', name: 'Quantité par défaut', group: 'Quantité', description: 'Remet la quantité à sa valeur par défaut.' },

  // --- Sélection ---
  {
    id: 'com.trader.ninjatrader.instrument', name: 'Instrument', group: 'Sélection',
    description: 'Sélectionne l\'instrument de travail.',
    settings: [
      { key: 'instrument', label: 'Instrument', type: 'text', placeholder: 'MNQ', help: 'Racine du symbole, par exemple MNQ ou ES.' },
      { key: 'displayLabel', label: 'Libellé affiché', type: 'text', placeholder: 'vide = instrument' },
    ],
  },
  {
    id: 'com.trader.ninjatrader.account', name: 'Compte', group: 'Sélection',
    description: 'Fait défiler les comptes disponibles.',
    settings: [
      { key: 'accounts', label: 'Comptes (un par ligne)', type: 'textarea', placeholder: 'Sim101\nSim102', help: 'Vide = tous les comptes remontés par NinjaTrader.' },
    ],
  },

  // --- Affichage ---
  {
    id: 'com.trader.ninjatrader.status', name: 'Indicateur', group: 'Affichage',
    description: 'Touche d\'affichage seul, au contenu configurable.',
    settings: [
      {
        key: 'statusType', label: 'Contenu affiché', type: 'select',
        options: [
          { value: 'connection', label: 'Connexion NinjaTrader' },
          { value: 'account', label: 'Compte' },
          { value: 'instrument', label: 'Instrument' },
          { value: 'position', label: 'Position' },
          { value: 'pnl', label: 'P&L latent' },
          { value: 'quantity', label: 'Quantité' },
          { value: 'safety', label: 'Macro de sécurité' },
        ],
      },
    ],
  },
  {
    id: 'com.trader.ninjatrader.cooldown', name: 'Temporisation', group: 'Affichage',
    description: 'Bloque les entrées pendant un temps réglable après un trade perdant.',
    settings: [
      {
        key: 'cooldownSeconds', label: 'Durée (secondes)', type: 'number',
        min: 1, max: 3600, step: 1,
        help: 'Temps pendant lequel les entrées sont refusées après une perte. Les sorties '
            + 'restent toujours possibles. Bornes alignées sur le bridge : 1 s à 3600 s.',
      },
    ],
  },
  {
    id: 'com.trader.ninjatrader.safety', name: 'Macro de sécurité', group: 'Affichage',
    description: 'Arme la macro de sécurité et affiche son état.',
    settings: [
      { key: 'maxTradesWhenLosing', label: 'Trades max en perte', type: 'number', min: 0, max: 100, step: 1, help: '0 = pas de limite sur le nombre de trades.' },
      { key: 'dailyLossLimit', label: 'Perte journalière max', type: 'number', min: 0, help: 'En devise du compte. 0 = pas de limite.' },
      // min aligné sur SafetyMacro.MinLockHours : en dessous, le bridge refuse avec un
      // INVALID_PAYLOAD que rien n'expliquait à l'écran.
      { key: 'lockDurationHours', label: 'Durée du verrou (heures)', type: 'number', min: 0.05, max: 24, help: 'Le verrou ne peut pas être levé avant son expiration.' },
      {
        key: 'devMode',
        label: 'Mode développement',
        type: 'toggle',
        help: 'Permet de désarmer la macro en appuyant dessus, sans attendre la fin du verrou. '
            + 'Existe pour pouvoir la mettre au point — sinon chaque essai verrouille pour la durée '
            + 'complète. La touche affiche DEV tant que ce mode est actif. À laisser désactivé en séance.',
      },
    ],
  },

  // --- Automatisme ---
  {
    id: 'host.autobe', name: 'Auto BE', group: 'Position',
    description: 'Place le break-even automatiquement dès que le gain atteint un seuil. Crée le '
               + 'stop s\'il n\'existe pas. Se réarme à chaque renfort de position.',
    settings: [
      {
        key: 'triggerTicks', label: 'Déclenchement (ticks de gain)', type: 'number', min: 1, max: 500, step: 1,
        help: 'Nombre de ticks de gain au-delà du prix moyen avant de poser le break-even.',
      },
      {
        key: 'offsetTicks', label: 'Décalage du BE (ticks)', type: 'number', min: -100, max: 100, step: 1,
        help: 'Ajouté au prix moyen pour placer le stop. 0 = au point mort exact, positif = '
            + 'sécurise un gain, négatif = laisse respirer.',
      },
    ],
  },

  // --- Navigation ---
  // Assurée jusqu'ici par les touches « dossier » natives d'Elgato, qui n'existent plus ici.
  {
    id: 'host.navigate', name: 'Changer de page', group: 'Navigation',
    description: 'Bascule le deck vers une autre page.',
    settings: [
      {
        key: 'targetPage', label: 'Destination', type: 'select',
        // L'interface complète cette liste avec les pages réelles du layout : choisir une page
        // par son nom vaut mieux que retenir son numéro.
        options: [
          { value: 'next', label: 'Page suivante' },
          { value: 'prev', label: 'Page précédente' },
        ],
        help: 'Suivante et précédente bouclent en fin de liste.',
      },
      { key: 'label', label: 'Libellé sur la touche', type: 'text', placeholder: 'vide = automatique' },
    ],
  },
];

/**
 * Réglages proposés sur TOUTE touche, quelle que soit son action.
 *
 * Déclarés à part plutôt que recopiés dans les 24 entrées du catalogue : l'interface les ajoute
 * à la fin de chaque formulaire, et `host.ts` les lit dans les réglages de l'emplacement.
 */
/**
 * Durée de maintien quand la confirmation est active.
 *
 * Valeur unique et non réglable : choisir un nombre de millisecondes n'apporte rien au trader,
 * et laisser ce choix ouvert invitait à des valeurs trop courtes pour protéger ou trop longues
 * pour être tenues. 600 ms est assez long pour qu'un frôlement ne déclenche pas, assez court
 * pour ne pas paraître bloqué.
 */
export const HOLD_CONFIRM_MS = 600;

export const UNIVERSAL_SETTINGS: SettingField[] = [
  {
    key: 'holdConfirm',
    label: 'Confirmation par appui long',
    type: 'toggle',
    help: `Désactivé, la touche part au premier appui. Activé, il faut la maintenir `
        + `${HOLD_CONFIRM_MS} ms — une jauge se remplit, et relâcher avant annule.`,
  },
];

export const CATALOG_BY_ID = new Map(CATALOG.map((a) => [a.id, a]));

export function actionName(id: string): string {
  return CATALOG_BY_ID.get(id)?.name ?? id;
}
