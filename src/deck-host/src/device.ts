/**
 * Pilotage du boîtier — remplace l'application Stream Deck.
 *
 * Trois choses que l'application Elgato faisait pour nous et qu'il faut assumer ici :
 *  1. rasteriser le SVG (Elgato acceptait un data URI, le matériel veut du RGBA) ;
 *  2. maintenir la connexion USB (débranchement, veille/reprise) ;
 *  3. ne réécrire que ce qui change — le plugin repoussait les 15 touches à chaque cycle.
 */
import { listStreamDecks, openStreamDeck, type StreamDeck } from '@elgato-stream-deck/node';
import { Resvg } from '@resvg/resvg-js';
import { existsSync } from 'fs';
import { ButtonVisual } from './visuals.js';
import { renderButtonDataUri } from './render-node.js';
import * as log from './logger.js';

/**
 * Police épinglée : mesuré le 31/07/2026, `loadSystemFonts` par défaut fait rescanner toutes les
 * polices Windows à CHAQUE instanciation de Resvg — 95 ms par touche, soit 2 s pour un deck
 * complet. En épinglant un fichier on retombe à ~2 ms. Voir `docs/etude-hote-proprietaire.md` §11.
 *
 * Ne pas remplacer par `loadSystemFonts: false` seul : sans police chargée, resvg ne dessine
 * aucun texte — c'est rapide parce que la touche est vide.
 */
const FONT_CANDIDATES = [
  'C:\\Windows\\Fonts\\arialbd.ttf',
  'C:\\Windows\\Fonts\\segoeuib.ttf',
  'C:\\Windows\\Fonts\\arial.ttf',
];

function resolveFont(): string[] {
  const found = FONT_CANDIDATES.filter((f) => existsSync(f));
  if (found.length === 0) {
    log.eventWarn('Device', 'Aucune police épinglée trouvée — repli sur les polices système (lent)');
  }
  return found.slice(0, 1);
}

export interface DeckKeyEvent {
  index: number;
  column: number;
  row: number;
}

type PressHandler = (ev: DeckKeyEvent) => void;
type ConnectionHandler = (connected: boolean) => void;

/**
 * Le relâchement n'était pas écouté jusqu'ici : une action partait dès l'appui. Il devient
 * nécessaire pour l'appui long, où c'est le relâchement prématuré qui annule.
 */
type ReleaseHandler = (ev: DeckKeyEvent) => void;

export class DeckDevice {
  #deck: StreamDeck | null = null;
  #buttons: { index: number; column: number; row: number; pixelSize: { width: number; height: number } }[] = [];
  /** Dernier visuel réellement écrit, par emplacement — sert au rendu différentiel. */
  #painted = new Map<number, string>();
  #fontFiles = resolveFont();
  #pressHandlers: PressHandler[] = [];
  #releaseHandlers: ReleaseHandler[] = [];
  #connHandlers: ConnectionHandler[] = [];
  #reconnectTimer: ReturnType<typeof setTimeout> | null = null;
  #closing = false;
  #brightness = 80;

  get connected(): boolean {
    return this.#deck !== null;
  }

  get columns(): number {
    return this.#buttons.length ? Math.max(...this.#buttons.map((b) => b.column)) + 1 : 5;
  }

  get rows(): number {
    return this.#buttons.length ? Math.max(...this.#buttons.map((b) => b.row)) + 1 : 3;
  }

  get keyCount(): number {
    return this.#buttons.length;
  }

  get productName(): string {
    return this.#deck?.PRODUCT_NAME ?? 'aucun boîtier';
  }

  onPress(fn: PressHandler): void {
    this.#pressHandlers.push(fn);
  }

  onRelease(fn: ReleaseHandler): void {
    this.#releaseHandlers.push(fn);
  }

  onConnectionChange(fn: ConnectionHandler): void {
    this.#connHandlers.push(fn);
  }

  async start(): Promise<void> {
    this.#closing = false;
    await this.#tryOpen();
  }

  async #tryOpen(): Promise<void> {
    if (this.#closing || this.#deck) return;
    try {
      const decks = await listStreamDecks();
      if (decks.length === 0) {
        this.#scheduleReconnect('aucun boîtier détecté');
        return;
      }

      const deck = await openStreamDeck(decks[0].path, { resetToLogoOnClose: true });
      this.#deck = deck;
      this.#buttons = deck.CONTROLS.filter((c: any) => c.type === 'button').map((c: any) => ({
        index: c.index, column: c.column, row: c.row, pixelSize: c.pixelSize,
      }));
      this.#painted.clear();

      deck.on('down', (c: any) => {
        if (c.type !== 'button') return;
        for (const fn of this.#pressHandlers) fn({ index: c.index, column: c.column, row: c.row });
      });

      deck.on('up', (c: any) => {
        if (c.type !== 'button') return;
        for (const fn of this.#releaseHandlers) fn({ index: c.index, column: c.column, row: c.row });
      });

      // Une erreur HID signifie presque toujours que le périphérique a disparu (débranchement,
      // veille). On referme proprement pour repartir sur une reconnexion propre.
      deck.on('error', (err: unknown) => {
        log.fail('Device', err, 'Erreur HID — fermeture et reconnexion');
        void this.#handleLoss();
      });

      await deck.setBrightness(this.#brightness);
      await deck.clearPanel();

      log.event('Device', 'Boîtier connecté', {
        model: deck.MODEL, product: deck.PRODUCT_NAME,
        keys: this.#buttons.length, grid: `${this.columns}x${this.rows}`,
      });
      for (const fn of this.#connHandlers) fn(true);
    } catch (err) {
      // Cas le plus courant en développement : l'application Elgato tourne encore et tient
      // le périphérique. Message explicite plutôt qu'une trace HID illisible.
      log.fail('Device', err, "Ouverture du boîtier impossible — l'application Elgato est-elle ouverte ?");
      this.#scheduleReconnect('ouverture impossible');
    }
  }

  async #handleLoss(): Promise<void> {
    const deck = this.#deck;
    this.#deck = null;
    this.#painted.clear();
    for (const fn of this.#connHandlers) fn(false);
    if (deck) {
      try {
        await deck.close();
      } catch {
        /* le périphérique est déjà parti */
      }
    }
    this.#scheduleReconnect('périphérique perdu');
  }

  #scheduleReconnect(reason: string): void {
    if (this.#closing || this.#reconnectTimer) return;
    log.debugEvent('Device', 'Nouvelle tentative de connexion dans 3 s', { reason });
    this.#reconnectTimer = setTimeout(() => {
      this.#reconnectTimer = null;
      void this.#tryOpen();
    }, 3000);
  }

  setBrightness(value: number): void {
    this.#brightness = Math.max(0, Math.min(100, value));
    void this.#deck?.setBrightness(this.#brightness).catch(() => undefined);
  }

  /** Rasterise un visuel en RGBA à la taille des touches du boîtier. */
  #raster(visual: ButtonVisual, width: number): Buffer {
    const dataUri = renderButtonDataUri(visual);
    const svg = Buffer.from(dataUri.split(',')[1], 'base64').toString('utf8');
    return new Resvg(svg, {
      fitTo: { mode: 'width', value: width },
      font: { loadSystemFonts: false, fontFiles: this.#fontFiles, defaultFontFamily: 'Arial' },
    }).render().pixels;
  }

  /**
   * Écrit un visuel sur une touche, seulement s'il a changé.
   * Le plugin Elgato repoussait les 15 touches à chaque cycle : `lastVisualSignature` n'y
   * filtrait que la journalisation, pas l'envoi.
   */
  async paint(slot: number, visual: ButtonVisual | null): Promise<void> {
    const deck = this.#deck;
    if (!deck) return;
    const button = this.#buttons.find((b) => b.index === slot);
    if (!button) return;

    // `progress` fait partie de la signature : sans lui, la jauge d'appui long était calculée à
    // chaque tic mais jamais réécrite, le rendu différentiel la jugeant identique. Elle
    // s'affichait une fois puis restait figée.
    const signature = visual
      ? `${visual.title}|${visual.subtitle ?? ''}|${visual.detail ?? ''}|${visual.bgColor}|${visual.textColor}|${visual.badge ?? ''}|${visual.progress ?? ''}`
      : 'VIDE';
    if (this.#painted.get(slot) === signature) return;

    // Le rastérisage est isolé de l'écriture USB : un SVG que resvg refuse est un défaut de
    // contenu, pas une perte de périphérique. Les confondre transformait une touche mal libellée
    // en boucle infinie de déconnexion/reconnexion du boîtier.
    let pixels: Buffer | null = null;
    if (visual) {
      try {
        pixels = this.#raster(visual, button.pixelSize.width);
      } catch (err) {
        log.fail('Visual', err, 'Rendu de touche impossible — touche laissée inchangée', {
          slot, title: visual.title,
        });
        return;
      }
    }

    try {
      if (!pixels) {
        await deck.fillKeyColor(slot, 0, 0, 0);
      } else {
        await deck.fillKeyBuffer(slot, pixels, { format: 'rgba' });
      }
      this.#painted.set(slot, signature);
      // Grâce au rendu différentiel, cette ligne ne paraît qu'au vrai changement : c'est la
      // seule trace qui distingue « l'aperçu de l'interface a changé » de « la touche physique
      // a été réécrite ». Sans elle, un visuel figé est indiagnosticable.
      log.debugEvent('Visual', 'Touche réécrite', {
        slot, title: visual?.title ?? '(vide)', sub: visual?.subtitle ?? '', bg: visual?.bgColor ?? '',
      });
    } catch (err) {
      this.#painted.delete(slot);
      log.fail('Device', err, 'Écriture de touche impossible', { slot });
      void this.#handleLoss();
    }
  }

  /** Force le redessin complet au prochain `paint` — après une reconnexion ou un changement de page. */
  invalidate(): void {
    this.#painted.clear();
  }

  async close(): Promise<void> {
    this.#closing = true;
    if (this.#reconnectTimer) clearTimeout(this.#reconnectTimer);
    const deck = this.#deck;
    this.#deck = null;
    if (deck) {
      try {
        await deck.close();
      } catch {
        /* ignore */
      }
    }
  }
}
