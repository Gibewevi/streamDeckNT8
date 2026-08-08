/**
 * Journal comportemental — le second flux de la journalisation TradeDeck.
 *
 * Les exécutions disent ce qui a été tradé ; ces événements-ci disent **comment**. Épisodes
 * d'Anti-Tilt, blocages du Guard, verrous, ordres passés hors du deck pendant un refus : ce sont
 * eux qui alimentent les statistiques psychologiques, et rien d'autre ne les connaît.
 *
 * Écrit par l'hôte et non par l'add-on, pour une raison simple : l'hôte reçoit déjà `stateUpdate`
 * enrichi du bloc sécurité par le bridge, et il connaît ses propres appuis. Le faire côté add-on
 * imposerait un nouveau canal entre les processus pour une information que l'hôte a sous la main.
 *
 * Les deux flux atterrissent dans le même dossier et repartiront par le même envoi : un seul
 * chemin à rendre sûr, un seul à surveiller.
 *
 * Rien ici ne doit pouvoir gêner le trading : aucune exception ne sort, aucun appel réseau.
 */
import { appendFileSync, mkdirSync, readdirSync, statSync, unlinkSync } from 'fs';
import { randomBytes } from 'crypto';
import { join } from 'path';
import { DEFAULT_DATA_DIR } from './layout.js';
import { Empreinte } from './transitions.js';
import * as log from './logger.js';

const JOURNAL_DIR = join(DEFAULT_DATA_DIR, 'journal');

/** Au-delà, un spool jamais envoyé est une fuite de disque plutôt qu'un filet de sécurité. */
const RETENTION_JOURS = 30;

/** Ce que porte une ligne du journal comportemental. */
export interface JournalEvent {
  kind: 'event';
  /**
   * Identifiant unique de l'événement.
   *
   * Les exécutions ont l'identifiant que NinjaTrader leur donne ; les événements n'ont rien de
   * tel. Sans lui, un lot renvoyé après une coupure — le cas normal, pas l'exception — créerait
   * des doublons, et « trois épisodes de tilt » deviendrait « neuf ».
   */
  eid: string;
  type: string;
  atUtc: string;
  account: string;
  instrument: string;
  [cle: string]: unknown;
}

export class EventRecorder {
  #dossier: string;
  #echecSignale = false;
  #dernierePurge = 0;

  constructor(dossier = JOURNAL_DIR) {
    this.#dossier = dossier;
  }

  /**
   * Écrit un événement.
   *
   * Écriture **synchrone**, délibérément : ces événements sont des transitions, pas un flux — on
   * en compte quelques-uns par minute, jamais par seconde. Une écriture synchrone de quelques
   * dizaines d'octets coûte moins qu'une milliseconde et supprime toute question de « a-t-on eu
   * le temps de vider le tampon avant le plantage ». C'est précisément après un incident que ce
   * journal a le plus de valeur.
   */
  record(type: string, contexte: { account?: string; instrument?: string }, donnees: Record<string, unknown> = {}): void {
    try {
      const evenement: JournalEvent = {
        kind: 'event',
        eid: randomBytes(9).toString('base64url'),
        type,
        atUtc: new Date().toISOString(),
        account: contexte.account || '',
        instrument: contexte.instrument || '',
        ...donnees,
      };

      mkdirSync(this.#dossier, { recursive: true });
      const jour = evenement.atUtc.slice(0, 10);
      appendFileSync(join(this.#dossier, `events-${jour}.ndjson`), `${JSON.stringify(evenement)}\n`, 'utf8');

      this.#purger();
      this.#echecSignale = false;
    } catch (err) {
      // Une fois, pas à chaque transition : un disque plein produirait autrement des milliers de
      // lignes identiques qui noieraient ce qui compte.
      if (!this.#echecSignale) {
        this.#echecSignale = true;
        log.fail('Journal', err, 'Journal comportemental indisponible — les événements ne sont pas enregistrés');
      }
    }
  }

  /**
   * Compare deux empreintes et enregistre ce qui a changé.
   *
   * Se greffe sur la détection de transitions qui existe déjà pour les logs : une seule
   * comparaison, deux consommateurs. Dupliquer la détection aurait garanti qu'elles finissent
   * par diverger.
   */
  observe(avant: Empreinte | null, apres: Empreinte): void {
    // Premier état reçu : rien à comparer, et enregistrer « armé » au démarrage fausserait le
    // décompte des activations.
    if (!avant) return;

    const ctx = { account: apres.account, instrument: apres.instrument };

    if (avant.safetyArmed !== apres.safetyArmed) {
      this.record(apres.safetyArmed ? 'guard.armed' : 'guard.disarmed', ctx);
    }

    // Le verrou borne le temps pendant lequel la protection ne peut pas être levée. Ses deux
    // bornes donnent la durée passée sous contrainte, qui est la mesure de discipline la plus
    // directe dont on dispose.
    if (avant.safetyLocked !== apres.safetyLocked) {
      this.record(apres.safetyLocked ? 'guard.locked' : 'guard.unlocked', ctx);
    }

    // Le Guard refuse les entrées. `blockReason` dit laquelle des trois limites a été atteinte —
    // c'est ce qui distingue une discipline tenue d'une limite subie.
    if (!avant.entriesBlocked && apres.entriesBlocked) {
      this.record('guard.blocked', ctx, { reason: apres.blockReason });
    }
    if (avant.entriesBlocked && !apres.entriesBlocked) {
      this.record('guard.unblocked', ctx, { reason: avant.blockReason });
    }

    if (!avant.tiltActive && apres.tiltActive) {
      this.record('tilt.started', ctx, { reason: apres.tiltReason });
    }
    if (avant.tiltActive && !apres.tiltActive) {
      this.record('tilt.ended', ctx, { reason: avant.tiltReason });
    }

    if (!avant.cooldownActive && apres.cooldownActive) {
      this.record('cooldown.started', ctx);
    }

    // Protection de la position. On ne l'enregistre que position ouverte : un stop qui disparaît
    // parce que la position vient d'être fermée n'est pas un stop retiré.
    if (apres.posExists) {
      if (!avant.hasStop && apres.hasStop) this.record('stop.placed', ctx, { stopPrice: apres.stopPrice });
      if (avant.hasStop && !apres.hasStop && avant.posExists) this.record('stop.removed', ctx);
    }

    // Ouverture et clôture de position : elles donnent les bornes de chaque aller-retour, ce qui
    // permet de rattacher un épisode de tilt au trade pendant lequel il s'est produit.
    if (!avant.posExists && apres.posExists) {
      this.record('position.opened', ctx, { direction: apres.posDirection, quantity: apres.posQuantity });
    }
    if (avant.posExists && !apres.posExists) {
      this.record('position.closed', ctx, { direction: avant.posDirection, quantity: avant.posQuantity });
    }
  }

  /**
   * Ordre passé **hors du deck** pendant que la macro refuse les entrées.
   *
   * C'est, par construction, une tentative de contourner sa propre protection — aucune autre
   * source ne donne cette information. `cancelled: false` dit que le contournement a réussi :
   * l'ordre s'était déjà exécuté quand l'add-on l'a vu.
   */
  recordViolation(v: {
    violation?: string; cancelled?: boolean; orderAction?: string;
    orderType?: string; quantity?: number; instrument?: string; name?: string;
  }, account: string): void {
    this.record('guard.violation', { account, instrument: v.instrument }, {
      reason: v.violation ?? '',
      cancelled: v.cancelled === true,
      orderAction: v.orderAction ?? '',
      orderType: v.orderType ?? '',
      quantity: v.quantity ?? 0,
      orderName: v.name ?? '',
    });
  }

  /**
   * Confirmation par appui long entamée puis relâchée avant la fin.
   *
   * Une hésitation mesurée : le trader a commencé le geste et s'est arrêté. C'est le seul
   * indicateur de doute que l'on puisse observer sans rien demander à personne.
   */
  recordHoldAbandoned(actionId: string, tenuMs: number, requisMs: number, ctx: { account: string; instrument: string }): void {
    this.record('hold.abandoned', ctx, { actionId, heldMs: Math.round(tenuMs), requiredMs: requisMs });
  }

  /** Une fois par jour au plus : parcourir un dossier à chaque transition serait absurde. */
  #purger(): void {
    const maintenant = Date.now();
    if (maintenant - this.#dernierePurge < 24 * 3600_000) return;
    this.#dernierePurge = maintenant;

    try {
      const limite = maintenant - RETENTION_JOURS * 24 * 3600_000;
      for (const nom of readdirSync(this.#dossier)) {
        if (!nom.startsWith('events-') || !nom.endsWith('.ndjson')) continue;
        const chemin = join(this.#dossier, nom);
        if (statSync(chemin).mtimeMs < limite) unlinkSync(chemin);
      }
    } catch {
      // Le ménage ne doit jamais coûter un événement : un fichier tenu par l'envoi est normal.
    }
  }
}
