/**
 * Déploiement de l'add-on dans NinjaTrader — le constat, pas l'action.
 *
 * Le voyant « NinjaTrader » de l'éditeur ne savait dire que `hors ligne`, ce qui recouvre trois
 * situations qu'on ne peut pas distinguer depuis Bitlearn : NinjaTrader pas installé, add-on
 * jamais déposé, ou add-on déposé mais pas encore compilé faute de redémarrage. Tant que l'add-on
 * n'est pas chargé, **rien du côté NinjaTrader ne parle** — ni journal, ni connexion sur le port
 * 8219. Trois clients, trois diagnostics impossibles à distinguer à distance.
 *
 * Ce module regarde ce que l'hôte peut voir sans NinjaTrader : les fichiers sur le disque. Il ne
 * déploie rien et ne corrige rien — c'est le travail de l'installateur.
 *
 * Règle de la maison : rien ici ne doit pouvoir retarder ou empêcher le trading. Aucune attente
 * sur le chemin de démarrage, aucune exception qui remonte, et un chemin de repli utilisable
 * immédiatement.
 */
import { execFile } from 'child_process';
import { existsSync, readdirSync } from 'fs';
import { join } from 'path';
import * as log from './logger.js';

/**
 * Ce que l'hôte remonte à Bitlearn. Valeurs stables : elles traversent le fil, se rangent en base
 * et s'affichent sous le voyant.
 */
export type EtatAddOn =
  /** Pas de `Documents\NinjaTrader 8\bin\Custom` : la plateforme n'est pas installée ici. */
  | 'NT_MISSING'
  /** NinjaTrader est là, l'add-on n'y a jamais été déposé. */
  | 'NOT_DEPLOYED'
  /** Des sources, mais pas celles qu'il faut — dépôt interrompu, ou copie manuelle partielle. */
  | 'INCOMPLETE'
  /** Tout est en place. NinjaTrader hors ligne malgré ça = il n'a pas encore recompilé. */
  | 'DEPLOYED'
  /** La question n'a pas encore été posée au disque. */
  | 'UNKNOWN';

/** Point d'entrée de l'add-on : sa présence est ce qui distingue « déposé » de « rien ». */
const FICHIER_PIVOT = 'StreamDeckAddOn.cs';

/**
 * Le moteur de swings vit dans `Indicators\`, pas avec l'add-on, et `TrendEngine` le référence.
 * Son absence casse la compilation de tout le reste : c'est un dépôt incomplet, pas un dépôt.
 */
const FICHIER_INDICATEUR = 'TdSwingEngine.cs';

/** Au-delà, on rejette un coup d'œil au disque. Deux `existsSync` — le coût est nul. */
const FRAICHEUR_MS = 60_000;

/**
 * Repli immédiat, remplacé dès que la base de registre a répondu.
 *
 * `%USERPROFILE%\Documents` est faux sur un poste où OneDrive a repris le dossier Documents, ce
 * qui est le réglage par défaut de beaucoup de machines neuves. L'installateur, lui, utilise la
 * constante Inno `{userdocs}`, qui suit la redirection : les deux doivent désigner le même
 * dossier, sans quoi l'hôte annoncerait « add-on non déposé » à côté de fichiers bien présents.
 */
let racine = join(process.env.USERPROFILE || '', 'Documents', 'NinjaTrader 8', 'bin', 'Custom');

let dernierConstat: EtatAddOn = 'UNKNOWN';
let constateA = 0;

/** Remplace `%VAR%` par sa valeur : la clé de registre est un `REG_EXPAND_SZ`. */
function etendreVariables(valeur: string): string {
  return valeur.replace(/%([^%]+)%/g, (tout, nom: string) => process.env[nom] ?? tout);
}

/**
 * Aligne la racine sur le dossier Documents réel, celui que lit l'Explorateur — et l'installateur.
 *
 * Lancé sans être attendu : le démarrage ne dépend pas de `reg.exe`, et le repli couvre les
 * quelques dizaines de millisecondes où la réponse n'est pas encore là.
 */
export function localiserNinjaScript(apres: () => void): void {
  execFile(
    'reg',
    ['query', 'HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\User Shell Folders', '/v', 'Personal'],
    { timeout: 2_000, windowsHide: true },
    (err, stdout) => {
      // `apres` est appelé quoi qu'il arrive : le repli est utilisable, et un constat sur le
      // mauvais dossier vaut mieux qu'un journal de démarrage muet sur la question.
      try {
        if (err) {
          log.debugEvent('NinjaTrader', 'Dossier Documents non résolu — repli sur %USERPROFILE%', {
            raison: err.message,
          });
          return;
        }

        const documents = etendreVariables((stdout.match(/Personal\s+REG_\w+\s+(.+)/)?.[1] || '').trim());
        if (!documents) return;

        const resolue = join(documents, 'NinjaTrader 8', 'bin', 'Custom');
        if (resolue === racine) return;

        racine = resolue;
        // Invalide le constat fait sur le repli : il portait peut-être sur le mauvais dossier.
        constateA = 0;
        log.debugEvent('NinjaTrader', 'Dossier Documents redirigé', { documents });
      } finally {
        apres();
      }
    },
  );
}

function constater(): EtatAddOn {
  if (!existsSync(racine)) return 'NT_MISSING';

  const dossierAddOn = join(racine, 'AddOns', 'StreamDeck');
  if (!existsSync(join(dossierAddOn, FICHIER_PIVOT))) {
    // Un dossier qui contient des `.cs` sans le pivot est un dépôt abîmé, pas un dépôt absent :
    // il compilera peut-être, et il faut le dire autrement.
    let autres = 0;
    try {
      autres = readdirSync(dossierAddOn).filter((n) => n.toLowerCase().endsWith('.cs')).length;
    } catch {
      autres = 0;
    }
    return autres > 0 ? 'INCOMPLETE' : 'NOT_DEPLOYED';
  }

  if (!existsSync(join(racine, 'Indicators', FICHIER_INDICATEUR))) return 'INCOMPLETE';

  return 'DEPLOYED';
}

/**
 * L'état du dépôt, tel qu'il part dans le battement.
 *
 * `ntConnecte` court-circuite tout : si l'add-on parle, il est évidemment déposé et compilé, et
 * il n'y a aucune raison d'aller lire le disque cinq fois par minute pour le confirmer.
 */
export function etatAddOn(ntConnecte: boolean): EtatAddOn {
  if (ntConnecte) return 'DEPLOYED';

  const maintenant = Date.now();
  if (dernierConstat !== 'UNKNOWN' && maintenant - constateA < FRAICHEUR_MS) return dernierConstat;

  try {
    dernierConstat = constater();
  } catch (err) {
    // Un disque qui refuse de répondre ne doit pas faire échouer un battement : on garde le
    // dernier constat, quitte à ce qu'il soit `UNKNOWN`.
    log.debugEvent('NinjaTrader', 'Constat du dépôt impossible', { raison: (err as Error)?.message });
  }
  constateA = maintenant;
  return dernierConstat;
}

/** Une phrase pour le journal de démarrage, là où on cherche quand un voyant reste rouge. */
export function journaliserEtat(ntConnecte: boolean): void {
  const etat = etatAddOn(ntConnecte);
  const details = { dossier: racine };

  switch (etat) {
    case 'DEPLOYED':
      log.event('NinjaTrader', 'Add-on déposé — NinjaTrader le compile à son démarrage', details);
      break;
    case 'NOT_DEPLOYED':
      log.eventWarn('NinjaTrader', 'Add-on absent du dossier NinjaScript — NinjaTrader restera hors ligne', details);
      break;
    case 'INCOMPLETE':
      log.eventWarn('NinjaTrader', 'Dépôt de l\'add-on incomplet — la compilation NinjaScript échouera', details);
      break;
    case 'NT_MISSING':
      log.eventWarn('NinjaTrader', 'NinjaTrader 8 introuvable sur ce poste', details);
      break;
    default:
      break;
  }
}
