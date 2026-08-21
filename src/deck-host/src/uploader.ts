/**
 * Envoi du journal vers Bitlearn — le transport entre les spools locaux et le serveur.
 *
 * Volontairement **hors du chemin de trading** : il vit dans l'hôte, pas dans l'add-on, et une
 * panne réseau ici n'a aucun effet sur l'envoi d'ordres. Au pire le journal prend du retard.
 *
 * Deux garanties gouvernent ce fichier :
 *
 *   - **On n'avance le curseur qu'après un accusé de réception.** Réémettre est gratuit — les
 *     index uniques côté serveur rendent un doublon impossible — alors que perdre une exécution
 *     est définitif. En cas de doute, on renvoie.
 *   - **On ne consomme que des lignes complètes.** Le collecteur écrit pendant qu'on lit ; une
 *     ligne coupée en deux serait rejetée par le serveur et perdue à jamais si le curseur avait
 *     déjà avancé.
 */
import { existsSync, mkdirSync, readdirSync, readFileSync, statSync, writeFileSync, openSync, readSync, closeSync } from 'fs';
import { join } from 'path';
import { DEFAULT_DATA_DIR } from './layout.js';
import { Layout } from './layout.js';
import { BitlearnClient } from './bitlearn.js';
import * as log from './logger.js';

const JOURNAL_DIR = join(DEFAULT_DATA_DIR, 'journal');

/** Aligné sur la limite du serveur : au-delà, il tronque et l'on croirait avoir tout envoyé. */
const MAX_LOT = 500;

/** Assez fréquent pour un journal quasi-live, assez rare pour ne rien coûter au réseau. */
const INTERVALLE_MS = 60_000;

const ACTION_COMPTE = 'com.trader.ninjatrader.account';

/**
 * Comptes dont les sessions remontent.
 *
 * `noms` n'est plus alimenté : le réglage qui le remplissait — une liste de comptes à saisir, un
 * par ligne — a été retiré de la touche Compte. La structure demeure parce qu'elle porte encore la
 * décision qui compte, marche ou arrêt : bascule éteinte, `tous` reste faux et `noms` vide, donc
 * rien ne quitte le poste.
 */
export interface FiltreComptes {
  tous: boolean;
  noms: Set<string>;
}

/**
 * Lit dans le layout si l'utilisateur a choisi de journaliser.
 *
 * La bascule vit sur la touche Compte : journaliser est une propriété du compte courtier, pas une
 * commande. Elle couvre tous les comptes que NinjaTrader remonte.
 *
 * `settings.accounts` n'est délibérément PLUS lu. Le réglage a disparu de l'écran, et une clé
 * restée dans un layout ancien restreindrait sinon la journalisation à des comptes que plus
 * personne ne voit ni ne peut corriger — des séances manqueraient sans que rien ne le dise.
 */
export function comptesJournalises(layout: Layout): FiltreComptes {
  const filtre: FiltreComptes = { tous: false, noms: new Set() };

  for (const page of layout.pages) {
    for (const slot of Object.values(page.slots)) {
      if (slot.actionId !== ACTION_COMPTE) continue;
      if (slot.settings?.journal !== true) continue;
      filtre.tous = true;
    }
  }

  return filtre;
}

const retenu = (compte: string, filtre: FiltreComptes) =>
  filtre.tous || filtre.noms.has(compte);

interface Curseurs {
  [fichier: string]: number;
}

export class JournalUploader {
  #bitlearn: BitlearnClient;
  #dossier: string;
  #curseurs: Curseurs = {};
  #timer: NodeJS.Timeout | null = null;
  #enCours = false;
  #getFiltre: () => FiltreComptes;

  /**
   * `getFiltre` est fourni ici et non à `start` : sans cela, un `flush` appelé avant le démarrage
   * ne trouvait aucun compte et ne faisait rien — en silence, ce qui est la pire façon de rater
   * un envoi. Relu à chaque envoi plutôt que capturé, pour que cocher la bascule prenne effet
   * sans redémarrer l'hôte.
   */
  /**
   * `getSolde` rend la cash value du compte sélectionné, ou null. Une fonction plutôt qu'une valeur :
   * le solde bouge à chaque trade, et l'envoyer figé au démarrage aurait été pire que ne rien
   * envoyer — un capital de départ faux est plus trompeur qu'un capital absent.
   */
  #getSolde: () => { compte: string; solde: number } | null;

  constructor(
    bitlearn: BitlearnClient,
    getFiltre: () => FiltreComptes,
    getSolde: () => { compte: string; solde: number } | null = () => null,
    dossier = JOURNAL_DIR,
  ) {
    this.#bitlearn = bitlearn;
    this.#getFiltre = getFiltre;
    this.#getSolde = getSolde;
    this.#dossier = dossier;
    this.#curseurs = this.#chargerCurseurs();
  }

  start(): void {
    if (this.#timer) return;

    // Au lancement d'abord : c'est le rattrapage de tout ce qui n'est pas parti pendant que le
    // poste était éteint, hors ligne, ou que Bitlearn ne répondait pas.
    void this.flush('lancement');
    this.#timer = setInterval(() => void this.flush('périodique'), INTERVALLE_MS);
    this.#timer.unref?.();
  }

  stop(): void {
    if (this.#timer) {
      clearInterval(this.#timer);
      this.#timer = null;
    }
  }

  /**
   * Envoie ce qui reste à envoyer.
   *
   * Réentrance interdite : un envoi déclenché par un retour à plat pendant qu'un envoi périodique
   * est en vol émettrait deux fois les mêmes lignes. Le serveur les dédupliquerait, mais les deux
   * curseurs se marcheraient dessus.
   */
  async flush(raison: string): Promise<void> {
    if (this.#enCours || !this.#bitlearn.paired) return;
    this.#enCours = true;

    try {
      const filtre = this.#getFiltre();
      // Rien de coché : on ne lit même pas les spools. La capture reste locale, ce qui est
      // exactement ce que promet la bascule — « désactivé, rien ne quitte ce PC ».
      if (!filtre.tous && filtre.noms.size === 0) return;

      const executions = this.#lire('executions-', filtre);
      const events = this.#lire('events-', filtre);
      const balances = this.#lire('balances-', filtre);

      if (executions.lignes.length === 0 && events.lignes.length === 0 && balances.lignes.length === 0) return;

      // Le solde n'accompagne que le compte que l'hôte a réellement sous les yeux. Deviner celui
      // des autres à partir de leur P&L reviendrait à inventer un capital de départ.
      //
      // Il continue de partir en champ de requête EN PLUS du spool scellé : c'est lui qui cale le
      // capital de départ du journal (`initial_balance = solde − P&L`), et le spool sert à autre
      // chose — la médiane du capital de référence et la réconciliation. Les retirer d'un coup
      // aurait cassé l'affichage des pourcentages pour un gain nul.
      const courant = this.#getSolde();
      const soldes = courant && retenu(courant.compte, filtre)
        ? { [courant.compte]: courant.solde }
        : undefined;

      const envoye = await this.#bitlearn.sendJournal({
        executions: executions.lignes,
        events: events.lignes,
        balances: balances.lignes,
        soldes,
      });
      if (!envoye) return;

      // Après l'accusé seulement.
      const curseurs = { ...executions.curseurs, ...events.curseurs, ...balances.curseurs };
      for (const [fichier, offset] of Object.entries(curseurs)) {
        this.#curseurs[fichier] = offset;
      }
      this.#sauverCurseurs();

      // Le solde est journalisé avec le lot, et son ABSENCE aussi. Sans cette mention, un journal
      // ouvert à zéro chez Bitlearn ne se distinguait pas d'un journal correct : rien, nulle part,
      // ne disait que le capital de départ n'était jamais parti. Le défaut a vécu une version
      // entière sans laisser la moindre trace.
      log.event('Journal', 'Lot envoyé à Bitlearn', {
        executions: executions.lignes.length, evenements: events.lignes.length,
        soldesScelles: balances.lignes.length, raison,
        solde: soldes ? Object.entries(soldes).map(([c, s]) => `${c}=${s}`).join(',') : 'ABSENT',
      });
    } catch (err) {
      log.fail('Journal', err, 'Envoi du journal impossible');
    } finally {
      this.#enCours = false;
    }
  }

  /**
   * Lit les lignes non encore envoyées des spools d'un type donné.
   *
   * Ne rend que des lignes **complètes** : le collecteur écrit pendant qu'on lit, et une ligne
   * tronquée serait rejetée par le serveur puis perdue, le curseur ayant avancé.
   */
  #lire(prefixe: string, filtre: FiltreComptes): { lignes: unknown[]; curseurs: Curseurs } {
    const lignes: unknown[] = [];
    const curseurs: Curseurs = {};

    if (!existsSync(this.#dossier)) return { lignes, curseurs };

    // Les plus anciens d'abord : le journal doit arriver dans l'ordre où il s'est produit.
    const fichiers = readdirSync(this.#dossier)
      .filter((n) => n.startsWith(prefixe) && n.endsWith('.ndjson'))
      .sort();

    for (const nom of fichiers) {
      if (lignes.length >= MAX_LOT) break;

      const chemin = join(this.#dossier, nom);
      const depart = this.#curseurs[nom] ?? 0;
      const taille = statSync(chemin).size;
      if (taille <= depart) continue;

      const brut = this.#lireDepuis(chemin, depart, taille);
      // `split` produit toujours un dernier élément après le dernier séparateur : la ligne
      // incomplète si le tampon ne finit pas par un saut de ligne, une chaîne vide s'il finit
      // par un. Dans les deux cas il ne correspond à aucune ligne à consommer — et le compter
      // faisait dériver le curseur d'un octet à chaque envoi, si bien que la lecture suivante
      // démarrait au milieu d'une ligne.
      const morceaux = brut.split('\n');
      morceaux.pop();

      let consomme = depart;
      for (const morceau of morceaux) {
        // +1 pour le saut de ligne : le curseur compte des octets, pas des caractères.
        const octets = Buffer.byteLength(morceau, 'utf8') + 1;
        if (!morceau.trim()) { consomme += octets; continue; }
        if (lignes.length >= MAX_LOT) break;

        try {
          const objet = JSON.parse(morceau) as { account?: string };
          // Le filtrage se fait ici, à l'envoi, et non à la capture : écrire sur le disque du
          // trader n'est pas une transmission.
          if (retenu(objet.account ?? '', filtre)) lignes.push(objet);
        } catch {
          // Ligne illisible : on la saute plutôt que de bloquer le spool derrière elle.
          log.eventWarn('Journal', 'Ligne de spool illisible ignorée', { fichier: nom });
        }
        consomme += octets;
      }

      curseurs[nom] = consomme;
    }

    return { lignes, curseurs };
  }

  /** Lecture partielle : recharger un fichier entier à chaque tour serait absurde en fin de journée. */
  #lireDepuis(chemin: string, depart: number, fin: number): string {
    const longueur = fin - depart;
    const tampon = Buffer.allocUnsafe(longueur);
    const fd = openSync(chemin, 'r');
    try {
      readSync(fd, tampon, 0, longueur, depart);
    } finally {
      closeSync(fd);
    }
    return tampon.toString('utf8');
  }

  /** Dans le dossier des spools, jamais un chemin figé : les deux doivent rester solidaires. */
  #cheminCurseurs(): string {
    return join(this.#dossier, 'cursor.json');
  }

  #chargerCurseurs(): Curseurs {
    try {
      const chemin = this.#cheminCurseurs();
      if (!existsSync(chemin)) return {};
      return JSON.parse(readFileSync(chemin, 'utf8')) as Curseurs;
    } catch {
      // Curseurs illisibles : on repart de zéro. Le serveur dédupliquera — c'est précisément à
      // ça que servent les index uniques.
      log.eventWarn('Journal', 'Curseurs illisibles — le spool sera réémis depuis le début');
      return {};
    }
  }

  #sauverCurseurs(): void {
    try {
      mkdirSync(this.#dossier, { recursive: true });
      writeFileSync(this.#cheminCurseurs(), JSON.stringify(this.#curseurs, null, 2), 'utf8');
    } catch (err) {
      // Non bloquant : au pire on réémettra: le serveur dédupliquera.
      log.eventWarn('Journal', 'Curseurs non enregistrés', { raison: (err as Error)?.message });
    }
  }
}
