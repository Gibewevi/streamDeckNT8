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
import { Sealer } from './seal.js';
import * as log from './logger.js';

const JOURNAL_DIR = join(DEFAULT_DATA_DIR, 'journal');

/** Au-delà, un spool jamais envoyé est une fuite de disque plutôt qu'un filet de sécurité. */
const RETENTION_JOURS = 30;

/**
 * Espacement minimal entre deux échantillons de solde.
 *
 * Le solde arrive avec l'état, cinq fois par seconde. L'échantillonner à cette cadence produirait
 * des centaines de milliers de lignes par jour pour une médiane qui n'en demande que quelques
 * dizaines — c'est le même piège que journaliser dans une boucle périodique.
 */
const INTERVALLE_SOLDE_MS = 5 * 60_000;

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
  /** Jour de bourse du bridge — l'unité sur laquelle Bitlearn découpe une séance. */
  tradingDay?: string;
  [cle: string]: unknown;
}

export class EventRecorder {
  #dossier: string;
  #echecSignale = false;
  #dernierePurge = 0;
  #sealer: Sealer;
  #dernierSolde = 0;
  /**
   * Dernier jour de bourse vu dans l'état du bridge.
   *
   * Tenu ici plutôt que passé à chaque appel : `recordViolation` et `recordHoldAbandoned` sont
   * appelés depuis des endroits qui n'ont pas l'état sous la main, et un événement sans jour de
   * bourse sortirait de la séance à laquelle il appartient — donc du calcul d'XP qui la note.
   */
  #tradingDay = '';

  constructor(dossier = JOURNAL_DIR, sealer?: Sealer) {
    this.#dossier = dossier;
    this.#sealer = sealer ?? new Sealer(dossier);
  }

  /** La clé arrive de Bitlearn à l'appairage ; l'add-on la relira dans le même dossier. */
  installerCle(cleHex: string): void {
    this.#sealer.installerCle(cleHex);
  }

  get scelle(): boolean {
    return this.#sealer.actif;
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
        tradingDay: this.#tradingDay,
        ...donnees,
      };

      // Scellé AVANT écriture : la ligne signée est celle qui touche le disque, sinon il resterait
      // une fenêtre pendant laquelle le fichier contient du non signé.
      this.#sealer.sceller('event', evenement as unknown as Record<string, unknown>);

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
    // Retenu même au tout premier état : les événements suivants en dépendent, et un `guard.armed`
    // sans jour de bourse sortirait de la séance qu'il ouvre.
    this.#tradingDay = apres.tradingDay || this.#tradingDay;

    // Premier état reçu : rien à comparer, et enregistrer « armé » au démarrage fausserait le
    // décompte des activations.
    if (!avant) return;

    const ctx = { account: apres.account, instrument: apres.instrument };

    if (avant.safetyArmed !== apres.safetyArmed) {
      // L'armement porte les limites EN VIGUEUR, et c'est la seule occasion de les capturer.
      // Sans elles, « respect des limites » n'a pas de dénominateur côté Bitlearn : un Guard armé
      // avec toutes ses limites à zéro n'aurait jamais rien refusé et se lirait comme une séance
      // sans faute — soit exactement la configuration qui ne protège de rien.
      const limites = apres.safetyArmed
        ? {
            dailyLossLimit: apres.dailyLossLimit,
            maxTradesWhenLosing: apres.maxTradesWhenLosing,
            maxContracts: apres.maxContracts,
            pauseAfterMinutes: apres.pauseAfterMinutes,
          }
        : {};
      this.record(apres.safetyArmed ? 'guard.armed' : 'guard.disarmed', ctx, limites);
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
   * Échantillon de la cash value du compte.
   *
   * Troisième flux du journal, et le dernier arrivé — le solde partait jusqu'ici dans un champ
   * libre du corps de requête, appliqué au journal puis oublié. Ni historique, ni scellé, ni
   * rapprochable. Or c'est la grandeur dont dépend le capital de référence de l'XP, donc la SEULE
   * que ce poste puisse falsifier avec un effet direct sur le score d'une séance.
   *
   * L'écrire comme les autres lignes lui donne les deux protections qui manquaient : le sceau, et
   * un historique sur lequel Bitlearn prend une médiane — qu'une valeur isolée ne déplace pas.
   *
   * Limité en fréquence : le solde bouge à chaque trade, mais un échantillon toutes les cinq
   * minutes suffit largement à une médiane et à une réconciliation. Le publier à 5 Hz remplirait
   * le spool de plusieurs centaines de milliers de lignes par jour — le même piège que les logs
   * dans une boucle périodique.
   */
  recordBalance(compte: string, solde: number): void {
    if (!compte || !Number.isFinite(solde)) return;
    const maintenant = Date.now();
    if (maintenant - this.#dernierSolde < INTERVALLE_SOLDE_MS) return;
    this.#dernierSolde = maintenant;

    try {
      const ligne: Record<string, unknown> = {
        kind: 'balance',
        account: compte,
        balance: solde,
        atUtc: new Date(maintenant).toISOString(),
        tradingDay: this.#tradingDay,
      };
      this.#sealer.sceller('balance', ligne);

      mkdirSync(this.#dossier, { recursive: true });
      const jour = new Date(maintenant).toISOString().slice(0, 10);
      appendFileSync(join(this.#dossier, `balances-${jour}.ndjson`), `${JSON.stringify(ligne)}\n`, 'utf8');
    } catch (err) {
      // Un solde manqué ne coûte qu'un point de mesure de plus ou de moins dans une médiane : il
      // ne mérite pas d'être signalé à chaque fois.
      log.debugEvent('Journal', 'Échantillon de solde non enregistré', { raison: (err as Error)?.message });
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
        const spool = nom.startsWith('events-') || nom.startsWith('balances-');
        if (!spool || !nom.endsWith('.ndjson')) continue;
        const chemin = join(this.#dossier, nom);
        if (statSync(chemin).mtimeMs < limite) unlinkSync(chemin);
      }
    } catch {
      // Le ménage ne doit jamais coûter un événement : un fichier tenu par l'envoi est normal.
    }
  }
}
