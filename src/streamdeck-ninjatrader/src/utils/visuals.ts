/**
 * Visual rendering utilities for Stream Deck buttons.
 * 
 * Uses canvas-free SVG-based image generation for dynamic button visuals.
 * The Stream Deck SDK accepts base64-encoded SVG/PNG for setImage().
 * 
 * This module generates SVG strings that encode color, text, and state.
 */

// Color palette — trading cockpit theme
export const Colors = {
  // Backgrounds
  buyGreen: '#1B8A2E',
  buyGreenDim: '#0D4517',
  sellRed: '#C62828',
  sellRedDim: '#5C1111',
  flattenOrange: '#E65100',
  flattenOrangeDim: '#6D2600',
  cancelYellow: '#F9A825',
  cancelYellowDim: '#6D4A00',
  reverseViolet: '#7B1FA2',
  reverseVioletDim: '#3A0E4D',
  beBlue: '#1565C0',
  beBlueDim: '#0A2F5C',
  stopAmber: '#FF8F00',
  stopAmberDim: '#6D3D00',
  targetTeal: '#00838F',
  targetTealDim: '#003D42',
  qtySlate: '#455A64',
  qtySlateDim: '#1C2529',
  qtyActive: '#00ACC1',
  instrumentIndigo: '#283593',
  instrumentActive: '#3F51B5',
  statusDark: '#212121',
  connected: '#4CAF50',
  disconnected: '#F44336',
  disabled: '#424242',
  textWhite: '#FFFFFF',
  textDim: '#757575',
  textGold: '#FFD54F',
} as const;

export interface ButtonVisual {
  title: string;
  bgColor: string;
  textColor: string;
  subtitle?: string;
  subtitleColor?: string;
  badge?: string;       // small indicator top-right
  badgeColor?: string;
}

/**
 * Generate a background-only SVG for setImage() (no text — text is handled by setTitle).
 * Returns a data URI suitable for setImage().
 */
export function renderButtonSvg(visual: ButtonVisual): string {
  const bg = visual.bgColor;

  // Badge overlay (optional small dot top-right)
  let badgeSvg = '';
  if (visual.badge) {
    const bc = visual.badgeColor || Colors.connected;
    badgeSvg = `<circle cx="60" cy="12" r="6" fill="${bc}"/>`;
  }

  // Arrow overlay for stop/target/BE +/- buttons — centered on 144x144 canvas
  let arrowSvg = '';
  if (visual.title.startsWith('QTY_STOP_') || visual.title.startsWith('QTY_TARGET_') || visual.title.startsWith('QTY_BE_')) {
    const isUp = visual.title.endsWith('_UP');
    arrowSvg = isUp
      ? `<polygon points="72,30 104,70 40,70" fill="#AAAAAA" opacity="0.6"/>`
      : `<polygon points="72,114 104,74 40,74" fill="#AAAAAA" opacity="0.6"/>`;
  }

  const svg = `<svg xmlns="http://www.w3.org/2000/svg" width="144" height="144">
  <rect width="144" height="144" rx="12" fill="${bg}"/>
  ${arrowSvg}
  ${badgeSvg}
</svg>`;

  return `data:image/svg+xml;base64,${Buffer.from(svg).toString('base64')}`;
}

/**
 * Build the title string for setTitle(). Uses \n for line breaks.
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
  // Standard 2-line title
  return visual.subtitle ? `${visual.title}\n${visual.subtitle}` : visual.title;
}


