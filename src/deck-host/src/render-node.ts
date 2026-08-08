/**
 * Emballage des visuels côté Node — tout ce que `visuals.ts` ne peut pas contenir.
 *
 * `visuals.ts` fait partie de l'ensemble partagé avec Bitlearn : il est copié tel quel dans un
 * paquet Next.js et doit tourner dans un navigateur. `Buffer` n'y existe pas, et `setTitle()` n'a
 * de sens que pour le SDK Elgato. Ces deux dépendances vivent donc ici, dans un fichier que seul
 * l'hôte importe.
 */

import { ButtonVisual, renderButtonSvg } from './visuals.js';

/**
 * Le SVG d'une touche, emballé en data URI pour `setImage()` du SDK Elgato et pour les aperçus
 * de l'interface locale, qui les pose dans un `<img src>`.
 */
export function renderButtonDataUri(visual: ButtonVisual): string {
  const svg = renderButtonSvg(visual);
  return `data:image/svg+xml;base64,${Buffer.from(svg).toString('base64')}`;
}

/**
 * Titre passé à `setTitle()` du SDK Elgato. Les sauts de ligne y sont des `\n`.
 *
 * Sans appelant aujourd'hui — le dessin passe entièrement par l'image. Conservé plutôt que
 * supprimé parce qu'il encode la correspondance entre chaque disposition et sa lecture en texte,
 * ce qui redevient nécessaire dès qu'un modèle de boîtier sans écran apparaît.
 */
export function buildTitle(visual: ButtonVisual): string {
  const isQty = visual.title.startsWith('QTY_');
  if (isQty) {
    if (visual.title === 'QTY_CANCEL') {
      const count = visual.subtitle || '0';
      const hasItems = count !== '0';
      return hasItems ? `CLOSE\n${count}` : 'CLOSE\n0';
    }
    if (visual.title === 'QTY_PLUS') return `Qty\n+\n${visual.subtitle || ''}`;
    if (visual.title === 'QTY_MINUS') return `Qty\n−\n${visual.subtitle || ''}`;
    if (visual.title === 'QTY_RESET') return `Qty\nReset\n${visual.subtitle || ''}`;
    // Stop/Target/BE arrows
    const isStop = visual.title.startsWith('QTY_STOP_');
    const isBE = visual.title.startsWith('QTY_BE_');
    const label = isStop ? 'Stop' : isBE ? 'BE' : 'Target';
    return `${label}\n${visual.subtitle || ''}`;
  }
  if (visual.title.startsWith('SAFETY')) {
    const word = visual.title.split(':')[1] || 'GUARD';
    return [word, visual.subtitle, visual.detail].filter(Boolean).join('\n');
  }
  if (visual.title === 'COUNTDOWN') return visual.subtitle || '';
  // Standard 2-line title
  const sub = `${visual.subtitle ?? ''}${visual.subtitleAccent ?? ''}`;
  return sub ? `${visual.title}\n${sub}` : visual.title;
}


