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
  /**
   * Valeur posée à la création de la touche, et affichée tant que le champ n'a pas été touché.
   * Sans elle, un champ numérique naît à `min`, ce qui vaut souvent « règle désactivée » — un
   * défaut muet que personne ne remarque.
   */
  default?: number | string | boolean;
  /**
   * N'affiche ce champ que si un autre réglage vaut la valeur indiquée.
   *
   * Sert à ne pas montrer un réglage qui ne s'applique pas dans le contexte courant : un champ
   * visible mais sans effet est plus trompeur qu'un champ absent. L'hôte doit neutraliser en
   * conséquence le réglage masqué — masquer sans neutraliser laisserait une règle active et
   * invisible, ce qui est le pire des deux mondes.
   */
  showIf?: { key: string; equals: boolean | string | number };
  /**
   * Plancher applicable **aux valeurs non nulles**, 0 valant « règle désactivée ».
   *
   * Distinct de `min`, qui ne saurait pas exprimer « 0, ou bien 5 et plus ». Sans lui, l'éditeur
   * acceptait une valeur que le bridge relevait ensuite en silence : l'écran affichait 2 min et
   * la protection en appliquait 5.
   */
  floor?: number;
}

export interface ActionDef {
  /** Identifiant stable, stocké dans le layout. */
  id: string;
  /** Libellé dans la palette. */
  name: string;
  /** Regroupement dans la palette. */
  group: 'Ordres' | 'Position' | 'Quantité' | 'Stop / Target' | 'Sélection' | 'Affichage' | 'Navigation';
  /**
   * Explication courte : infobulle dans la palette, et rappel en tête du tiroir de réglages.
   * Trois lignes au maximum à la largeur du tiroir — le détail va dans le `help` des champs.
   */
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
      // Porté par la touche Compte plutôt que par une action dédiée : journaliser est une
      // propriété du compte courtier, pas une commande que l'on déclenche.
      {
        key: 'journal', label: 'Journaliser ce compte', type: 'toggle',
        help: 'Remonte les sessions de ce compte vers Bitlearn : trades, statistiques et '
            + 'comportement. Le journal créé est privé, publiable comme les autres. '
            + 'Désactivé, rien ne quitte ce PC.',
      },
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
      // Règle Guard et non Anti-Tilt : c'est une limite de risque objective, donc elle REFUSE
      // l'ordre au lieu de le ralentir. Seuls les ordres qui augmentent la position sont refusés —
      // ne jamais enfermer le trader dans une position trop grosse.
      {
        key: 'maxContracts', label: 'Contrats maximum', type: 'number', min: 0, max: 1000, step: 1,
        help: 'Refuse les entrées qui porteraient la position au-delà. Réduire reste toujours possible. 0 = pas de limite.',
      },
      { key: 'dailyLossLimit', label: 'Perte journalière max', type: 'number', min: 0, help: 'En devise du compte. 0 = pas de limite.' },
      // La seule règle qui ENVOIE un ordre au lieu d'en refuser un. Désactivée par défaut, et ce
      // n'est pas négociable : un logiciel qui se met à passer des ordres au marché après une mise
      // à jour que personne n'a demandée n'a rien à faire sur un compte.
      {
        key: 'autoFlattenOnDailyLoss', label: 'Clôturer tout à la perte max', type: 'toggle',
        help: 'Ferme toutes les positions du compte et annule tous les ordres dès que la perte '
            + 'journalière est atteinte. La journée est terminée : les entrées restent refusées ensuite.',
      },
      {
        key: 'autoFlattenGraceSeconds', label: 'Tolérance avant clôture (s)', type: 'number',
        min: 1, max: 60, step: 1, default: 5,
        showIf: { key: 'autoFlattenOnDailyLoss', equals: true },
        help: 'Le seuil doit rester franchi pendant ce délai. Le P&L de séance inclut le latent : '
            + 'sans ce délai, une simple mèche liquiderait le compte au plus mauvais prix.',
      },
      // min aligné sur SafetyMacro.MinLockHours : en dessous, le bridge refuse avec un
      // INVALID_PAYLOAD que rien n'expliquait à l'écran.
      { key: 'lockDurationHours', label: 'Durée du verrou (heures)', type: 'number', min: 0.05, max: 24, help: 'Le verrou ne peut pas être levé avant son expiration.' },
      // --- Anti-Tilt ---
      // Trois réglages, pas onze. Les seuils (escalade de taille, restitution de gain, série de
      // pertes, durée d'épisode, durée de maintien) sont dérivés par le bridge des deux limites
      // ci-dessus et des règles usuelles de gestion du risque. Demander à quelqu'un de calibrer
      // lui-même la protection contre ses propres impulsions revient à la lui faire desserrer au
      // moment où il en a le plus besoin — et un formulaire trop long finit simplement ignoré.
      {
        key: 'antiTiltEnabled', label: 'Anti-Tilt', type: 'toggle',
        help: 'Exige un appui long de 20 s sur les entrées après une escalade de taille, une '
            + 'restitution de gain ou une série de pertes. Désactivé, les déclenchements sont journalisés.',
      },
      {
        key: 'tiltAveragingAllowed', label: 'Moyennage autorisé', type: 'toggle', default: true,
        help: 'Désactivé, renforcer une position perdante met les entrées sous appui long jusqu\'à sa clôture.',
      },
      // --- Réglages avancés ---
      // En dernier et repliés : ce sont deux durées de confort, pas des règles. Les seuils qui
      // décident vraiment (escalade de taille, restitution de gain, série de pertes) restent hors
      // de portée même ici — les rendre modifiables reviendrait à laisser desserrer la protection
      // au moment précis où elle sert.
      {
        key: 'tiltAdvanced', label: 'Réglages avancés', type: 'toggle',
        help: 'Ajuster les durées de l\'Anti-Tilt. Désactivé, elles reviennent à 20 s et 15 min.',
      },
      {
        key: 'tiltHoldSeconds', label: 'Durée de l\'appui long (s)', type: 'number', min: 15, max: 30, step: 1,
        default: 20, showIf: { key: 'tiltAdvanced', equals: true },
        help: 'Maintien exigé sur une entrée ralentie. Relâcher avant la fin annule.',
      },
      {
        key: 'tiltEpisodeMinutes', label: 'Durée de l\'épisode (min)', type: 'number', min: 1, max: 60, step: 1,
        default: 15, showIf: { key: 'tiltAdvanced', equals: true },
        help: 'Temps pendant lequel les entrées restent ralenties après un déclenchement.',
      },
      // Il n'existe volontairement AUCUN réglage de contournement du verrou. Un « mode
      // développement » a existé ici, désarmant la macro sans attendre son expiration ; il est
      // retiré. Une protection dont l'utilisateur détient l'interrupteur ne protège que tant qu'il
      // n'en a pas besoin — et c'est précisément au moment où elle sert que l'on cherche
      // l'interrupteur. Le verrou n'est donc levé que par son échéance.
    ],
  },

  // --- Pause obligatoire ---
  // Macro à part entière, et non un réglage de Guard. Ce n'est pas une règle de risque mais une
  // règle d'endurance : elle ne mesure pas ce qu'on perd, elle mesure depuis combien de temps on
  // trade. Elle s'applique donc que Guard soit armé ou non — une touche posée et réglée qui ne
  // ferait rien tant qu'une AUTRE macro n'est pas armée serait un piège.
  //
  // Le compteur démarre au PREMIER TRADE, jamais à l'ouverture de la séance : regarder le marché
  // ne fatigue pas. Et tout arrêt d'au moins la durée de la pause remet le compteur à zéro, ce qui
  // fait de la nuit, du week-end et du déjeuner de simples trous — aucun cas particulier à écrire.
  {
    id: 'com.trader.ninjatrader.pause', name: 'Pause obligatoire', group: 'Affichage',
    description: 'Impose une coupure après un temps de trading. Affiche le décompte, puis la '
               + 'pause en cours. Clôturer et réduire restent toujours possibles.',
    settings: [
      {
        key: 'pauseAfterMinutes', label: 'Pause après (min de trading)', type: 'number',
        min: 0, max: 480, step: 5, default: 0, floor: 5,
        help: 'Minutes de trading, comptées depuis le premier trade, avant qu\'une pause ne devienne '
            + 'obligatoire. 0 = pas de pause. En dessous de 5 min, ramené à 5.',
      },
      {
        key: 'pauseDurationMinutes', label: 'Durée de la pause (min)', type: 'number',
        min: 1, max: 120, step: 1, default: 10,
        help: 'Durée de la coupure. Le temps déjà passé sans trader compte dedans : revenir de '
            + 'déjeuner ne déclenche pas une pause supplémentaire. Seules les entrées sont refusées.',
      },
    ],
  },

  // --- Tendance ---
  // Touche d'AFFICHAGE, dans cette version. Elle ne s'arme pas et ne refuse rien : elle montre le
  // sens du marché en permanence, et le bridge journalise ce qu'un filtre aurait refusé. Un
  // interrupteur qui ne fait rien serait un piège — l'armement arrivera avec le refus, une fois ces
  // chiffres lus sur une vraie séance.
  //
  // Rien ne dépend du graphique du trader : l'add-on charge ses propres barres. La macro fonctionne
  // graphique fermé, workspace changé, et quel que soit le type de bougies affiché.
  //
  // Le sens vient de la STRUCTURE — cassure du dernier sommet ou creux — et il n'y a pas d'autre
  // méthode à choisir. Un mode Heikin Ashi a existé ici : il répondait moins bien à la même
  // question (la couleur d'une bougie bascule à chaque pullback, et la formule n'a rien à régler)
  // pour le prix d'un réglage de plus sur la touche et d'un champ de plus dans le protocole.
  {
    id: 'com.trader.ninjatrader.trend', name: 'Tendance', group: 'Affichage',
    description: 'Affiche le sens du marché sur deux unités de temps. Peut, en option, refuser les '
               + 'entrées qui vont à son encontre — maintenir la touche pour armer.',
    settings: [
      // Interrupteur de CAPACITÉ, pas d'armement. Décoché, la touche reste indicative et le
      // maintien ne fait rien. Coché, elle devient armable — et l'armement, lui, se fait sur le
      // boîtier, en séance, par un maintien.
      //
      // Faux par défaut, et ce n'est pas négociable : une macro capable de refuser des ordres ne
      // doit jamais s'activer parce qu'une mise à jour est passée. Même règle que la clôture
      // automatique à la perte max.
      {
        key: 'blocageAutorise', label: 'Autoriser le blocage des entrées', type: 'toggle', default: false,
        help: 'Décoché, la touche est un simple indicateur. Coché, la MAINTENIR l\'arme : les '
            + 'entrées à contre-tendance sont alors refusées. Clôturer et réduire restent toujours '
            + 'possibles, et un nouveau maintien désarme.',
      },
      {
        key: 'referenceMinutes', label: 'Unité de temps (min)', type: 'number',
        min: 1, max: 240, step: 1, default: 1,
        help: 'Unité principale, celle qui donne le sens.',
      },
      {
        key: 'higherEnabled', label: 'Confirmer par une unité supérieure', type: 'toggle', default: true,
        help: 'Les deux unités doivent pointer dans le même sens. En désaccord, la touche affiche FLAT.',
      },
      {
        key: 'higherMinutes', label: 'Unité supérieure (min)', type: 'number',
        min: 1, max: 1440, step: 1, default: 5,
        showIf: { key: 'higherEnabled', equals: true },
        help: 'Doit être STRICTEMENT supérieure à l\'unité de référence, sinon elle ne confirme rien.',
      },
      // Le seul réglage qui demande à être calibré, et c'est exactement ce que cette version sert à
      // faire : lire dans le journal combien de fois le sens bascule par heure.
      {
        key: 'thresholdAtr', label: 'Seuil de vague (× ATR)', type: 'number',
        min: 0.1, max: 10, default: 1,
        help: 'Amplitude minimale d\'une vague, en multiples d\'ATR. Exprimé ainsi, le même réglage '
            + 'tient sur toutes les unités de temps et tous les instruments. Plus haut = moins de '
            + 'changements de sens.',
      },
    ],
  },

  // --- Automatisme ---
  {
    id: 'host.autobe', name: 'Auto BE', group: 'Position',
    // Tenue courte : elle sert de rappel en tête du tiroir de réglages, où trois lignes sont un
    // maximum. Le sens de comptage vaut pour les deux champs et est redit dans chaque `help`.
    description: 'Place le break-even automatiquement dès que le gain atteint un seuil. Crée le '
               + 'stop s\'il n\'existe pas, et se réarme à chaque renfort de position.',
    settings: [
      {
        key: 'triggerTicks', label: 'Déclenchement (ticks de gain)', type: 'number', min: 1, max: 500, step: 1,
        help: 'Ticks de GAIN au-delà du prix moyen avant de poser le break-even. Compté dans le '
            + 'sens de la position : 60 se déclenche 60 ticks au-dessus de votre entrée en long, '
            + '60 ticks en dessous en short.',
      },
      // Le décalage doit rester SOUS le déclenchement. Égal ou supérieur, le break-even se poserait
      // au-delà du marché à l'instant même du déclenchement : NinjaTrader refuse, l'automatisme
      // retente cinq fois puis abandonne, et la position reste sans protection alors que la touche
      // annonce le contraire. L'hôte refuse désormais ce réglage ; le dire ici évite de le
      // découvrir en séance.
      {
        key: 'offsetTicks', label: 'Décalage du BE (ticks)', type: 'number', min: -100, max: 100, step: 1,
        help: 'Où placer le stop par rapport au prix moyen, compté dans le sens de la position. '
            + 'Positif = sécurise un gain, dans les deux sens : 4 place le stop 4 ticks AU-DESSUS '
            + 'de l\'entrée en long, et 4 ticks EN DESSOUS en short. 0 = point mort exact. '
            + 'Doit rester INFÉRIEUR au déclenchement, sinon le break-even est refusé à chaque fois. '
            + 'ATTENTION, négatif place le stop EN PERTE — ce n\'est plus un break-even.',
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
