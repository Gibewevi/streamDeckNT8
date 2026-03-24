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
  buyGreen: '#00C853',
  buyGreenDim: '#1B8A2E',
  sellRed: '#FF1744',
  sellRedDim: '#C62828',
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
 * Generate a full SVG with background, text, and overlays for setImage().
 * All text is rendered inside the SVG for full control over color and layout.
 * Returns a data URI suitable for setImage().
 */
export function renderButtonSvg(visual: ButtonVisual): string {
  const bg = visual.bgColor;
  const tc = visual.textColor || '#FFFFFF';
  const sub = visual.subtitle || '';
  const sc = visual.subtitleColor || tc;
  const t = visual.title;

  // Badge overlay (optional small dot top-right)
  let badgeSvg = '';
  if (visual.badge) {
    const bc = visual.badgeColor || Colors.connected;
    badgeSvg = `<circle cx="124" cy="20" r="8" fill="${bc}"/>`;
  }

  let contentSvg = '';

  if (t.startsWith('QTY_STOP_') || t.startsWith('QTY_TARGET_') || t.startsWith('QTY_BE_')) {
    // Arrow layout — label at top, medium centered triangle with value overlay, "ticks" at bottom
    const isUp = t.endsWith('_UP');
    const isStop = t.startsWith('QTY_STOP_');
    const isBE = t.startsWith('QTY_BE_');
    const label = isStop ? 'Stop' : isBE ? 'BE' : 'Target';
    const arrow = isUp
      ? `<polygon points="72,42 108,96 36,96" fill="#AAAAAA" opacity="0.55"/>`
      : `<polygon points="72,96 108,42 36,42" fill="#AAAAAA" opacity="0.55"/>`;
    // Same bounding box y=42..96, centroid at 1/3 from base: up=78, down=60
    const textY = isUp ? 78 : 60;
    contentSvg = `
      <text x="72" y="28" text-anchor="middle" font-family="sans-serif" font-size="24" font-weight="bold" fill="${tc}">${label}</text>
      ${arrow}
      <text x="72" y="${textY}" text-anchor="middle" dominant-baseline="central" font-family="sans-serif" font-size="24" font-weight="bold" fill="${tc}">${sub}</text>
      <text x="72" y="124" text-anchor="middle" font-family="sans-serif" font-size="18" fill="#000000">ticks</text>`;

  } else if (t === 'QTY_CANCEL') {
    const count = sub || '0';
    contentSvg = `
      <text x="72" y="42" text-anchor="middle" font-family="sans-serif" font-size="26" font-weight="bold" fill="${tc}">CLOSE</text>
      <text x="72" y="90" text-anchor="middle" font-family="sans-serif" font-size="40" font-weight="bold" fill="${tc}">${count}</text>
      <text x="72" y="120" text-anchor="middle" font-family="sans-serif" font-size="16" fill="${sc}">orders</text>`;

  } else if (t === 'QTY_PLUS' || t === 'QTY_MINUS') {
    const sign = t === 'QTY_PLUS' ? '+' : '\u2212';
    contentSvg = `
      <text x="72" y="36" text-anchor="middle" font-family="sans-serif" font-size="22" font-weight="bold" fill="${tc}">Qty</text>
      <text x="72" y="90" text-anchor="middle" font-family="sans-serif" font-size="48" font-weight="bold" fill="${tc}">${sign}</text>
      <text x="72" y="130" text-anchor="middle" font-family="sans-serif" font-size="30" font-weight="bold" fill="${tc}">${sub}</text>`;

  } else if (t === 'QTY_RESET') {
    contentSvg = `
      <text x="72" y="42" text-anchor="middle" font-family="sans-serif" font-size="22" font-weight="bold" fill="${tc}">Qty</text>
      <text x="72" y="78" text-anchor="middle" font-family="sans-serif" font-size="22" font-weight="bold" fill="${tc}">Reset</text>
      <text x="72" y="120" text-anchor="middle" font-family="sans-serif" font-size="30" font-weight="bold" fill="${tc}">${sub}</text>`;

  } else {
    // Standard layout: title centered, subtitle below
    if (sub) {
      contentSvg = `
        <text x="72" y="62" text-anchor="middle" font-family="sans-serif" font-size="34" font-weight="bold" fill="${tc}">${t}</text>
        <text x="72" y="100" text-anchor="middle" font-family="sans-serif" font-size="20" fill="${sc}">${sub}</text>`;
    } else {
      contentSvg = `
        <text x="72" y="84" text-anchor="middle" font-family="sans-serif" font-size="34" font-weight="bold" fill="${tc}">${t}</text>`;
    }
  }

  const svg = `<svg xmlns="http://www.w3.org/2000/svg" width="144" height="144">
  <rect width="144" height="144" rx="12" fill="${bg}"/>
  ${contentSvg}
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


