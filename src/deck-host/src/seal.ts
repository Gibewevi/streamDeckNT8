/**
 * Scellement du journal — le jumeau exact de `lib/tradeDeck/integrity.js` côté Bitlearn.
 *
 * Le spool est un fichier texte sur le disque du trader. Sans sceau, l'éditer avant l'envoi est
 * trivial et invisible, et l'XP que Bitlearn accorde à une séance se fabriquerait au bloc-notes.
 *
 * Chaque ligne porte `seq` et `sig = HMAC-SHA256(clé, seq ‖ sigPrécédente ‖ forme canonique)`. Le
 * chaînage est ce qui compte : une signature seule empêche de modifier une ligne, le chaînage
 * empêche aussi d'en supprimer, d'en insérer et d'en réordonner.
 *
 * **Ce que ça n'achète pas.** La clé vit sur cette machine. Qui l'extrait peut forger une chaîne
 * cohérente. Le sceau fait passer la fraude d'un éditeur de texte à de la rétro-ingénierie ; il ne
 * l'annule pas, et il ne sert à rien d'empiler de l'obfuscation par-dessus. Ce qui rend le
 * dispositif tenable est ailleurs, côté serveur : réconciliation du solde et XP qui ne paie jamais
 * au tarif fort une donnée invérifiable.
 *
 * ⚠ **Toute modification ici doit être reportée à l'identique dans
 * `Bitlearn/lib/tradeDeck/integrity.js`.** Les deux calculent la même chaîne d'octets ; s'ils
 * divergent d'un caractère, plus rien ne se scelle et tous les journaux tombent au palier non
 * vérifiable — silencieusement, puisque le poste croit signer correctement.
 */
import { createHmac } from 'crypto';
import { existsSync, mkdirSync, readFileSync, writeFileSync } from 'fs';
import { join } from 'path';
import * as log from './logger.js';

/** Unit separator : un caractère de contrôle ne peut pas venir d'une valeur JSON, donc aucun
 *  contenu ne peut simuler une frontière de champ. */
const SEP = '\u001f';

/** Les trois flux, un par écrivain. `exec` appartient à l'add-on, les deux autres à l'hôte. */
export type Flux = 'exec' | 'event' | 'balance';

/**
 * Format numérique unique, partagé par les trois langages du projet.
 *
 * Huit décimales puis retrait des zéros de queue. En C# : `ToString("0.########",
 * InvariantCulture)`. Un `JSON.stringify` canonique n'aurait pas tenu — `2.0` ne s'imprime pas de
 * la même façon partout.
 */
function nombreCanonique(valeur: unknown): string {
  if (valeur === null || valeur === undefined || valeur === '') return '';
  const n = Number(valeur);
  if (!Number.isFinite(n)) return '';
  const fixe = n.toFixed(8);
  return fixe.replace(/\.?0+$/, '') || '0';
}

function texteCanonique(valeur: unknown): string {
  if (valeur === null || valeur === undefined) return '';
  if (typeof valeur === 'boolean') return valeur ? 'true' : 'false';
  if (typeof valeur === 'number') return nombreCanonique(valeur);
  return String(valeur);
}

const CHAMPS_EXEC = [
  'execId', 'orderId', 'account', 'instrument', 'marketPosition', 'price', 'quantity',
  'commission', 'pointValue', 'tickSize', 'orderName', 'trend', 'time', 'recordedAtUtc',
  'tradingDay',
];
const CHAMPS_EVENEMENT = ['eid', 'type', 'atUtc', 'account', 'instrument', 'tradingDay'];
const CHAMPS_SOLDE = ['account', 'balance', 'atUtc', 'tradingDay'];
const CHAMPS_STRUCTURE = new Set([...CHAMPS_EVENEMENT, 'kind', 'seq', 'sig']);

/**
 * Forme canonique : une liste de champs FIXE, jamais un JSON réordonné.
 *
 * La ligne traverse deux sérialisations avant d'être vérifiée — elle est écrite, relue par
 * l'uploader, puis resérialisée par HTTP. La chaîne d'octets d'origine est perdue ; seule une
 * forme recalculable depuis l'objet analysé peut l'être des deux côtés.
 *
 * La charge utile d'un événement est signée elle aussi, triée par clé. Sans elle on pourrait
 * changer le motif d'un blocage ou l'issue d'un contournement sans casser la signature —
 * c'est-à-dire exactement les champs dont dépend l'XP.
 */
export function formeCanonique(ligne: Record<string, unknown>): string {
  const kind = ligne.kind === 'event' ? 'event' : ligne.kind === 'balance' ? 'balance' : 'exec';
  const champs = kind === 'event' ? CHAMPS_EVENEMENT : kind === 'balance' ? CHAMPS_SOLDE : CHAMPS_EXEC;

  const parties = [kind, ...champs.map((cle) => texteCanonique(ligne[cle]))];

  if (kind === 'event') {
    const charge = Object.keys(ligne)
      .filter((cle) => !CHAMPS_STRUCTURE.has(cle))
      .sort()
      .map((cle) => `${cle}=${texteCanonique(ligne[cle])}`);
    parties.push(charge.join(','));
  }

  return parties.join(SEP);
}

interface EtatFlux {
  seq: number;
  sig: string;
}

/**
 * Tient la clé et l'état des chaînes de l'hôte.
 *
 * L'état est persisté à côté des spools : redémarrer l'hôte ne doit pas casser une chaîne, sans
 * quoi chaque relance coûterait le sceau d'une séance.
 */
export class Sealer {
  #dossier: string;
  #cle: Buffer | null = null;
  #etats = new Map<Flux, EtatFlux>();
  #echecSignale = false;

  constructor(dossier: string) {
    this.#dossier = dossier;
    this.#charger();
  }

  get actif(): boolean {
    return this.#cle !== null;
  }

  /**
   * Enregistre la clé rendue par Bitlearn à l'appairage.
   *
   * Écrite dans le dossier du journal et non à côté du jeton, pour une raison précise : l'add-on
   * NinjaTrader doit pouvoir la lire. C'est un autre processus, dans une autre plateforme, et le
   * seul terrain qu'ils partagent est ce dossier — que l'add-on écrit déjà.
   *
   * Le choix est assumé : la clé est en clair sur le disque du trader. Elle y serait de toute
   * façon, sous une forme ou une autre, puisque c'est ce poste qui signe.
   */
  installerCle(cleHex: string): void {
    if (!cleHex || !/^[0-9a-f]{64}$/i.test(cleHex)) return;
    try {
      mkdirSync(this.#dossier, { recursive: true });
      writeFileSync(this.#cheminCle(), cleHex, 'utf8');
      this.#cle = Buffer.from(cleHex, 'hex');
      log.event('Journal', 'Clé de scellement installée — le journal part désormais scellé');
    } catch (err) {
      log.fail('Journal', err, 'Clé de scellement non enregistrée — le journal partira non scellé');
    }
  }

  /**
   * Scelle une ligne, sur place.
   *
   * Ne lève jamais : un scellement raté écrit la ligne NON SIGNÉE plutôt que de la perdre. Perdre
   * une exécution est définitif, perdre un sceau ne coûte que le palier d'intégrité d'une séance.
   */
  sceller(flux: Flux, ligne: Record<string, unknown>): void {
    if (!this.#cle) return;
    try {
      const etat = this.#etats.get(flux) ?? { seq: 0, sig: '' };
      // `seq` sort de l'horloge et non d'un compteur : si ce fichier d'état disparaît, un compteur
      // reparti de 1 condamnerait l'appareil à ne plus jamais rien sceller — le serveur lit tout
      // ce qui est en dessous du dernier seq accepté comme un rejeu. Une horloge, elle, continue
      // d'avancer toute seule. Le `+1` garantit la stricte croissance quand deux lignes tombent
      // dans la même milliseconde.
      const seq = Math.max(Date.now(), etat.seq + 1);
      ligne.seq = seq;
      const sig = createHmac('sha256', this.#cle)
        .update(`${seq}${SEP}${etat.sig}${SEP}${formeCanonique(ligne)}`)
        .digest('hex');
      ligne.sig = sig;
      this.#etats.set(flux, { seq, sig });
      this.#sauver();
    } catch (err) {
      if (!this.#echecSignale) {
        this.#echecSignale = true;
        log.fail('Journal', err, 'Scellement impossible — les lignes partiront non signées');
      }
      delete ligne.seq;
      delete ligne.sig;
    }
  }

  #cheminCle(): string {
    return join(this.#dossier, 'journal.key');
  }

  #cheminEtat(): string {
    return join(this.#dossier, 'seal-state.json');
  }

  #charger(): void {
    try {
      if (existsSync(this.#cheminCle())) {
        const hex = readFileSync(this.#cheminCle(), 'utf8').trim();
        if (/^[0-9a-f]{64}$/i.test(hex)) this.#cle = Buffer.from(hex, 'hex');
      }
    } catch (err) {
      log.eventWarn('Journal', 'Clé de scellement illisible — le journal partira non scellé', {
        raison: (err as Error)?.message,
      });
    }

    try {
      if (!existsSync(this.#cheminEtat())) return;
      const brut = JSON.parse(readFileSync(this.#cheminEtat(), 'utf8')) as Record<string, EtatFlux>;
      for (const flux of ['event', 'balance'] as Flux[]) {
        const etat = brut[flux];
        if (etat && Number.isFinite(etat.seq)) this.#etats.set(flux, { seq: etat.seq, sig: etat.sig ?? '' });
      }
    } catch {
      // État perdu : la première ligne du prochain lot cassera la chaîne une fois, le serveur se
      // resynchronisera dessus, et tout ce qui suit sera scellé de nouveau. Une séance abîmée, pas
      // un appareil condamné.
      log.eventWarn('Journal', 'État de scellement illisible — une rupture de chaîne est attendue');
    }
  }

  #sauver(): void {
    try {
      mkdirSync(this.#dossier, { recursive: true });
      writeFileSync(this.#cheminEtat(), JSON.stringify(Object.fromEntries(this.#etats), null, 2), 'utf8');
    } catch {
      // Non bloquant : au pire une rupture de chaîne au prochain démarrage.
    }
  }
}
