/**
 * Visual rendering utilities for Stream Deck buttons.
 *
 * Uses canvas-free SVG-based image generation for dynamic button visuals.
 * The Stream Deck SDK accepts base64-encoded SVG/PNG for setImage().
 *
 * This module generates SVG strings that encode color, text, and state.
 */
export declare const Colors: {
    readonly buyGreen: "#1DA81D";
    readonly buyGreenDim: "#0F5C0F";
    readonly sellRed: "#D13B3B";
    readonly sellRedDim: "#7A2222";
    readonly flattenOrange: "#E65100";
    readonly flattenOrangeDim: "#6D2600";
    readonly cancelYellow: "#F9A825";
    readonly cancelYellowDim: "#6D4A00";
    readonly reverseViolet: "#7B1FA2";
    readonly reverseVioletDim: "#3A0E4D";
    readonly beBlue: "#1565C0";
    readonly beBlueDim: "#0A2F5C";
    readonly stopAmber: "#FF8F00";
    readonly stopAmberDim: "#6D3D00";
    readonly targetTeal: "#00838F";
    readonly targetTealDim: "#003D42";
    readonly qtySlate: "#455A64";
    readonly qtySlateDim: "#1C2529";
    readonly qtyActive: "#00ACC1";
    readonly instrumentIndigo: "#283593";
    readonly instrumentActive: "#3F51B5";
    readonly statusDark: "#212121";
    readonly connected: "#4CAF50";
    readonly disconnected: "#F44336";
    readonly disabled: "#424242";
    readonly textWhite: "#FFFFFF";
    readonly textDim: "#757575";
    readonly textGold: "#FFD54F";
};
export interface ButtonVisual {
    title: string;
    bgColor: string;
    textColor: string;
    subtitle?: string;
    subtitleColor?: string;
    detail?: string;
    badge?: string;
    badgeColor?: string;
}
/**
 * Generate a full SVG with background, text, and overlays for setImage().
 * All text is rendered inside the SVG for full control over color and layout.
 * Returns a data URI suitable for setImage().
 */
export declare function renderButtonSvg(visual: ButtonVisual): string;
/**
 * Build the title string for setTitle(). Uses \n for line breaks.
 */
export declare function buildTitle(visual: ButtonVisual): string;
