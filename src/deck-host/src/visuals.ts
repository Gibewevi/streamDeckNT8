/**
 * Visual rendering utilities for Stream Deck buttons.
 * 
 * Uses canvas-free SVG-based image generation for dynamic button visuals.
 * The Stream Deck SDK accepts base64-encoded SVG/PNG for setImage().
 * 
 * This module generates SVG strings that encode color, text, and state.
 */

/**
 * Charte du deck — trois couleurs, plus une.
 *
 *   ORANGE : la touche est engagée — un achat, l'instrument suivi, un automatisme armé.
 *   BLANC  : utilitaire neutre — quantité, ajustement d'une protection, fermeture.
 *   NOIR   : au repos, éteint, inactif.
 *   ROUGE  : la SEULE couleur d'alerte conservée, et elle ne dit qu'une chose — cette touche
 *            REFUSE de partir.
 *
 * Le rouge survit à la charte pour une raison précise : le noir devient ici la couleur la plus
 * fréquente du deck, et un refus qui se lirait « noir au texte grisé » se confondrait avec une
 * touche simplement au repos. C'est l'information la plus coûteuse à manquer en séance.
 *
 * Achat et vente ne se distinguent plus par la teinte mais par l'INVERSION : l'achat est orange
 * plein, la vente est noire à titre orange. Le vert et le rouge d'origine sont ainsi libérés,
 * le rouge pouvant alors ne signifier que le refus.
 */
export const Colors = {
  orange: '#F07520',
  /** Orange atténué : le bridge ou NinjaTrader manque, la touche ne peut rien produire. */
  orangeDim: '#6B3410',
  white: '#FFFFFF',
  /** Noir des touches, volontairement au-dessus du noir pur qui « troue » l'écran du boîtier. */
  black: '#141414',

  /** Refus. Ne jamais l'employer pour autre chose, sous peine de lui retirer son sens. */
  refuse: '#D13B3B',

  textWhite: '#FFFFFF',
  textBlack: '#000000',
  textDim: '#6E6E6E',
} as const;

export interface ButtonVisual {
  title: string;
  bgColor: string;
  textColor: string;
  subtitle?: string;
  subtitleColor?: string;
  /**
   * Fin de sous-titre rendue dans une autre couleur, à la suite du sous-titre et sur la même
   * ligne. Sert à la quantité des touches d'entrée (« Sell » blanc, « ×1 » orange), que la
   * charte accentue pour la rendre lisible d'un coup d'œil sans agrandir le texte.
   */
  subtitleAccent?: string;
  accentColor?: string;
  detail?: string;      // third line, used by the 3-line layouts
  badge?: string;       // small indicator top-right
  badgeColor?: string;
  /** 0..1 — hold-to-confirm progress. Drawn as a bar at the bottom, over any layout. */
  progress?: number;
}

/**
 * Un SVG est du XML : un « & » ou un « < » non échappé rend l'image impossible à parser et fait
 * lever resvg. Les libellés viennent de l'interface de configuration (`displayLabel` est un champ
 * texte libre) et de NinjaTrader — jamais d'une source sûre. Un trader nommant une touche « S&P »
 * suffisait à faire échouer le rendu, et `DeckDevice.paint` interprétait cet échec comme une perte
 * du boîtier : le deck tombait en boucle de reconnexion.
 */
const esc = (value: unknown): string =>
  String(value).replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');

/**
 * Options de sortie. Le boîtier veut 144×144 en dur ; un navigateur veut une image qui suit la
 * taille de sa case. Le `viewBox` étant toujours émis, les deux lisent le même dessin.
 */
export interface RenderOptions {
  width?: number | string;
  height?: number | string;
}

/**
 * Rend le **markup** SVG d'une touche : fond, texte et surcouches.
 *
 * Rend une chaîne et non un data URI, et c'est ce qui rend ce fichier isomorphe : `Buffer` n'existe
 * pas dans un navigateur. L'emballage en data URI vit dans `render-node.ts`, côté hôte seul —
 * l'éditeur Bitlearn, lui, injecte ce markup directement.
 */
export function renderButtonSvg(visual: ButtonVisual, opts: RenderOptions = {}): string {
  const bg = esc(visual.bgColor);
  const tc = esc(visual.textColor || '#FFFFFF');
  const sub = esc(visual.subtitle || '');
  const sc = esc(visual.subtitleColor || visual.textColor || '#FFFFFF');
  // `t` sert aussi aux comparaisons de disposition ci-dessous : il est échappé au moment de
  // l'interpolation, pas ici, pour ne pas fausser les `startsWith`.
  const t = visual.title;

  // Badge en haut à droite. Un libellé de plusieurs caractères est rendu dans une pastille
  // lisible : un simple point coloré ne dit pas ce qu'il signale, ce qui est inutile pour
  // marquer un mode qui lève une protection.
  let badgeSvg = '';
  if (visual.badge) {
    const bc = esc(visual.badgeColor || Colors.orange);
    if (visual.badge.length <= 1) {
      badgeSvg = `<circle cx="124" cy="20" r="8" fill="${bc}"/>`;
    } else {
      const w = 16 + visual.badge.length * 11;
      badgeSvg = `
        <rect x="${138 - w}" y="6" width="${w}" height="26" rx="8" fill="${bc}"/>
        <text x="${138 - w / 2}" y="19" text-anchor="middle" dominant-baseline="central" font-family="sans-serif" font-size="16" font-weight="bold" fill="#000000">${esc(visual.badge)}</text>`;
    }
  }

  let contentSvg = '';

  if (t.startsWith('QTY_STOP_') || t.startsWith('QTY_TARGET_') || t.startsWith('QTY_BE_')) {
    // Arrow layout — label at top, medium centered triangle with value overlay, "ticks" at bottom
    const isUp = t.endsWith('_UP');
    const isStop = t.startsWith('QTY_STOP_');
    const isBE = t.startsWith('QTY_BE_');
    const label = isStop ? 'Stop' : isBE ? 'BE' : 'Target';
    // Triangle PLEIN de la couleur du texte, chiffre creusé dedans dans la couleur du fond.
    // L'ancien triangle gris translucide avec le chiffre par-dessus se lisait mal : à 144 px le
    // contraste d'un aplat vaut mieux qu'une superposition.
    const arrow = isUp
      ? `<polygon points="72,40 110,98 34,98" fill="${tc}"/>`
      : `<polygon points="72,98 110,40 34,40" fill="${tc}"/>`;
    const textY = isUp ? 84 : 68;
    contentSvg = `
      <text x="72" y="28" text-anchor="middle" font-family="sans-serif" font-size="24" font-weight="bold" fill="${tc}">${label}</text>
      ${arrow}
      <text x="72" y="${textY}" text-anchor="middle" dominant-baseline="central" font-family="sans-serif" font-size="24" font-weight="bold" fill="${bg}">${sub}</text>
      <text x="72" y="126" text-anchor="middle" font-family="sans-serif" font-size="18" fill="${tc}">ticks</text>`;

  } else if (t === 'QTY_CANCEL') {
    // Le nombre est la TAILLE DE LA POSITION, pas un nombre d'ordres : la mention « orders »
    // était fausse et prêtait à confusion avec la touche « Annuler ordres » voisine, qui elle
    // compte bien des ordres.
    const count = sub || '0';
    contentSvg = `
      <text x="72" y="42" text-anchor="middle" font-family="sans-serif" font-size="26" font-weight="bold" fill="${tc}">CLOSE</text>
      <text x="72" y="90" text-anchor="middle" font-family="sans-serif" font-size="40" font-weight="bold" fill="${tc}">${count}</text>
      <text x="72" y="120" text-anchor="middle" font-family="sans-serif" font-size="16" fill="${sc}">qty</text>`;

  } else if (t === 'QTY_PLUS' || t === 'QTY_MINUS') {
    const sign = t === 'QTY_PLUS' ? '+' : '\u2212';
    contentSvg = `
      <text x="72" y="36" text-anchor="middle" font-family="sans-serif" font-size="22" font-weight="bold" fill="${tc}">Qty</text>
      <text x="72" y="90" text-anchor="middle" font-family="sans-serif" font-size="48" font-weight="bold" fill="${tc}">${sign}</text>
      <text x="72" y="130" text-anchor="middle" font-family="sans-serif" font-size="30" font-weight="bold" fill="${tc}">${sub}</text>`;

  } else if (t === 'BE') {
    contentSvg = `
      <text x="72" y="58" text-anchor="middle" font-family="sans-serif" font-size="34" font-weight="bold" fill="${tc}">BE</text>
      <text x="72" y="100" text-anchor="middle" font-family="sans-serif" font-size="24" font-weight="bold" fill="${sc}">${sub}</text>`;

  } else if (t.startsWith('SAFETY')) {
    // Safety macro — status word ("SAFETY:<word>"), lock countdown, then trades / session P&L
    // Le mot-clé domine le sous-titre, comme sur toutes les autres touches : c'est lui qui dit
    // l'état, le décompte n'est qu'un complément. L'inverse donnait une touche où « OFF » criait
    // plus fort que « GUARD ».
    const word = esc(t.split(':')[1] || 'GUARD');
    contentSvg = `
      <text x="72" y="46" text-anchor="middle" font-family="sans-serif" font-size="30" font-weight="bold" fill="${tc}">${word}</text>
      <text x="72" y="88" text-anchor="middle" font-family="sans-serif" font-size="26" font-weight="bold" fill="${sc}">${sub}</text>
      <text x="72" y="122" text-anchor="middle" font-family="sans-serif" font-size="17" fill="${sc}">${esc(visual.detail || '')}</text>`;

  } else if (t.startsWith('TPSL')) {
    // Auto TP/SL — en-tête, état, puis les deux distances réglées.
    //
    // Trois lignes et non deux : l'état seul ne dit pas CE QUI sera posé, et c'est la question
    // qu'on se pose devant la touche avant d'entrer. Les distances tiennent sur la ligne du bas,
    // comme le restant de la touche Sécurité.
    const word = esc(t.split(':')[1] || 'OFF');
    contentSvg = `
      <text x="72" y="30" text-anchor="middle" font-family="sans-serif" font-size="22" font-weight="bold" fill="${tc}">TP/SL</text>
      <text x="72" y="82" text-anchor="middle" font-family="sans-serif" font-size="30" font-weight="bold" fill="${tc}">${word}</text>
      <text x="72" y="124" text-anchor="middle" font-family="sans-serif" font-size="17" fill="${sc}">${sub}</text>`;

  } else if (t.startsWith('TREND')) {
    // Tendance — mot, symbole, puis le détail par unité de temps.
    //
    // Un POLYGONE et non une flèche typographique, comme les touches Stop/Target au-dessus : le
    // SVG est rendu par resvg dans les polices du système, et un glyphe géométrique absent se
    // dessine en tofu — une touche illisible qu'aucun test de compilation ne signale. Un triangle
    // tracé ne dépend d'aucune police.
    const word = esc(t.split(':')[1] || 'FLAT');
    const symbol = word === 'UP'
      ? `<polygon points="72,48 106,96 38,96" fill="${tc}"/>`
      : word === 'DOWN'
        ? `<polygon points="72,96 106,48 38,48" fill="${tc}"/>`
        // Ni hausse ni baisse : une barre. Volontairement pas un triangle couché ou un point
        // d'interrogation — l'absence de direction doit se lire sans être déchiffrée.
        : `<rect x="38" y="65" width="68" height="13" fill="${tc}"/>`;
    contentSvg = `
      <text x="72" y="30" text-anchor="middle" font-family="sans-serif" font-size="22" font-weight="bold" fill="${tc}">TREND</text>
      ${symbol}
      <text x="72" y="128" text-anchor="middle" font-family="sans-serif" font-size="16" fill="${sc}">${sub}</text>`;

  } else if (t === 'COUNTDOWN') {
    // Cooldown countdown. Sous Elgato ce SVG restait vide : le gros chiffre venait de setTitle,
    // rendu nativement par l'application. L'hôte propriétaire n'a pas d'équivalent — sans ce
    // texte, la touche restait rouge et vide et le décompte semblait figé.
    // La taille s'adapte pour que trois chiffres tiennent encore dans la touche.
    const digits = sub.length;
    const size = digits >= 3 ? 58 : 72;
    contentSvg = `
      <text x="72" y="80" text-anchor="middle" dominant-baseline="central" font-family="sans-serif" font-size="${size}" font-weight="bold" fill="${tc}">${sub}</text>
      <text x="72" y="128" text-anchor="middle" font-family="sans-serif" font-size="18" fill="${sc}">sec</text>`;

  } else if (t === 'QTY_RESET') {
    contentSvg = `
      <text x="72" y="42" text-anchor="middle" font-family="sans-serif" font-size="22" font-weight="bold" fill="${tc}">Qty</text>
      <text x="72" y="78" text-anchor="middle" font-family="sans-serif" font-size="22" font-weight="bold" fill="${tc}">Reset</text>
      <text x="72" y="120" text-anchor="middle" font-family="sans-serif" font-size="30" font-weight="bold" fill="${tc}">${sub}</text>`;

  } else {
    // Standard layout: title centered, subtitle below
    if (sub || visual.subtitleAccent) {
      // Le `tspan` prolonge la même ligne : l'ancrage central s'applique à l'ensemble, la
      // quantité reste donc collée au mot qu'elle complète quel que soit son nombre de chiffres.
      const accent = visual.subtitleAccent
        ? `<tspan fill="${esc(visual.accentColor || visual.subtitleColor || tc)}">${esc(visual.subtitleAccent)}</tspan>`
        : '';
      contentSvg = `
        <text x="72" y="62" text-anchor="middle" font-family="sans-serif" font-size="34" font-weight="bold" fill="${tc}">${esc(t)}</text>
        <text x="72" y="100" text-anchor="middle" font-family="sans-serif" font-size="20" fill="${sc}">${sub}${accent}</text>`;
    } else {
      contentSvg = `
        <text x="72" y="84" text-anchor="middle" font-family="sans-serif" font-size="34" font-weight="bold" fill="${tc}">${esc(t)}</text>`;
    }
  }

  // Jauge de maintien : dessinée par-dessus la disposition, quelle qu'elle soit. C'est le seul
  // retour dont dispose le trader pour savoir que l'appui est pris en compte et combien il reste
  // à tenir — sans elle, une touche à confirmation paraît simplement ne pas répondre.
  let progressSvg = '';
  if (typeof visual.progress === 'number' && visual.progress > 0) {
    const p = Math.max(0, Math.min(1, visual.progress));
    progressSvg = `
      <rect x="10" y="126" width="124" height="8" rx="4" fill="#000000" opacity="0.45"/>
      <rect x="10" y="126" width="${(124 * p).toFixed(1)}" height="8" rx="4" fill="#FFFFFF"/>`;
  }

  // `viewBox` toujours présent : c'est lui qui rend le dessin indépendant de la taille de sortie.
  // resvg honore `width`/`height`, un navigateur peut demander « 100% » sans rien déformer.
  const width = opts.width ?? 144;
  const height = opts.height ?? 144;

  return `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 144 144" width="${width}" height="${height}">
  <rect width="144" height="144" rx="20" fill="${bg}"/>
  ${contentSvg}
  ${badgeSvg}
  ${progressSvg}
</svg>`;
}
