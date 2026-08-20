/**
 * Déploiement de l'add-on dans NinjaTrader — le constat, pas l'action.
 *
 * Le voyant « NinjaTrader » de l'éditeur ne savait dire que `hors ligne`, ce qui recouvre trois
 * situations qu'on ne peut pas distinguer depuis Bitlearn : NinjaTrader pas installé, add-on
 * jamais déposé, ou add-on déposé mais jamais compilé. Tant que l'add-on
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
import { existsSync, readdirSync, statSync } from 'fs';
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
  /**
   * Déposé, mais NinjaTrader tourne sur autre chose : assemblage plus ancien que les sources, ou
   * pas d'assemblage du tout. C'est le cas dangereux — il ne casse rien de visible, il fige
   * l'ancien add-on. Six versions ont ainsi été livrées à un poste qui tournait sur la
   * compilation du 12 août, voyant au vert, sans que rien ne le signale.
   */
  | 'STALE'
  /** Déposé et compilé : ce que NinjaTrader exécute correspond aux sources sur le disque. */
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
 * Valeur sous laquelle NinjaTrader publie sa racine `bin\Custom`, dans
 * `HKCU\Software\NinjaTrader, LLC\NinjaTrader\cmp<empreinte>`.
 *
 * C'est la plateforme elle-même qui l'écrit, une valeur par sous-clé. On la lui demande plutôt
 * que de la déduire : le dossier de données n'est pas toujours sous Documents, et rien
 * n'obligerait NinjaTrader à l'y laisser.
 */
const CLE_RACINE = 'PERSONAL_ROOT_BIN_CUSTOM';

/**
 * Repli immédiat, remplacé dès que la base de registre a répondu.
 *
 * `%USERPROFILE%\Documents` est faux sur un poste où OneDrive a repris le dossier Documents, ce
 * qui est le réglage par défaut de beaucoup de machines neuves. L'installateur suit la même
 * cascade : les deux doivent désigner le même dossier, sans quoi l'hôte annoncerait « add-on non
 * déposé » à côté de fichiers bien présents.
 */
let racine = join(process.env.USERPROFILE || '', 'Documents', 'NinjaTrader 8', 'bin', 'Custom');

/** D'où vient `racine`. Dans le journal de démarrage : sans elle, impossible de savoir si la
 *  plateforme a été interrogée ou si l'on a deviné — les deux donnent le même chemin sur un
 *  poste par défaut, et divergent silencieusement partout ailleurs. */
let origineRacine = 'repli %USERPROFILE%';

let dernierConstat: EtatAddOn = 'UNKNOWN';
let constateA = 0;

/** Remplace `%VAR%` par sa valeur : la clé de registre est un `REG_EXPAND_SZ`. */
function etendreVariables(valeur: string): string {
  return valeur.replace(/%([^%]+)%/g, (tout, nom: string) => process.env[nom] ?? tout);
}

/** Pose la racine et invalide le constat fait sur la précédente, qui visait un autre dossier. */
function adopterRacine(resolue: string, origine: string): void {
  origineRacine = origine;
  if (resolue === racine) return;
  racine = resolue;
  constateA = 0;
}

/**
 * Le dossier Documents réel, celui que lit l'Explorateur. Deuxième recours seulement : il ne
 * décrit où vit NinjaTrader que tant que NinjaTrader s'y trouve.
 */
function parLeDossierDocuments(apres: () => void): void {
  execFile(
    'reg',
    ['query', 'HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\User Shell Folders', '/v', 'Personal'],
    { timeout: 2_000, windowsHide: true },
    (err, stdout) => {
      try {
        if (err) {
          log.debugEvent('NinjaTrader', 'Dossier Documents non résolu — repli sur %USERPROFILE%', {
            raison: err.message,
          });
          return;
        }
        const documents = etendreVariables((stdout.match(/Personal\s+REG_\w+\s+(.+)/)?.[1] || '').trim());
        if (documents) adopterRacine(join(documents, 'NinjaTrader 8', 'bin', 'Custom'), 'dossier Documents');
      } finally {
        apres();
      }
    },
  );
}

/**
 * Demande à NinjaTrader où il range son NinjaScript, plutôt que de le déduire.
 *
 * Le dossier de données n'est `Documents\NinjaTrader 8` que par défaut, et le déduire fait
 * dépendre le dépôt d'une convention que rien ne garantit. La plateforme, elle, publie le chemin
 * exact. `/s` balaie les sous-clés — leurs noms sont des empreintes, il n'y a rien à deviner — et
 * `/v` ne rend que la valeur cherchée : une seule invocation, mesurée à 38 ms sur 476 sous-clés.
 *
 * Absente, la valeur signifie le plus souvent une plateforme installée mais jamais lancée. Son
 * dossier de données n'existe pas encore non plus, et le repli par Documents conclura de toute
 * façon à une absence — mais il conclura sur le bon dossier.
 *
 * Rien de tout cela n'est attendu par le démarrage : le repli code en dur sert entre-temps.
 */
export function localiserNinjaScript(apres: () => void): void {
  execFile(
    'reg',
    ['query', 'HKCU\\Software\\NinjaTrader, LLC\\NinjaTrader', '/s', '/v', CLE_RACINE],
    { timeout: 4_000, windowsHide: true },
    (err, stdout) => {
      // Plusieurs correspondances = plusieurs instances NinjaTrader sur le poste. On prend la
      // première : rien ne permet de designer « la bonne », et se tromper coûte un message,
      // pas une panne.
      const declaree = err
        ? ''
        : (stdout.match(new RegExp(CLE_RACINE + '\\s+REG_\\w+\\s+(.+)'))?.[1] || '').trim();

      if (declaree) {
        adopterRacine(declaree.replace(/[\\/]+$/, ''), 'registre NinjaTrader');
        apres();
        return;
      }

      log.debugEvent('NinjaTrader', 'NinjaTrader ne déclare pas sa racine — repli sur le dossier Documents');
      parLeDossierDocuments(apres);
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

  return compilationAJour(dossierAddOn) ? 'DEPLOYED' : 'STALE';
}

/**
 * L'assemblage compilé est-il plus récent que les sources déposées ?
 *
 * NinjaTrader compile tout `bin\Custom` dans un unique `NinjaTrader.Custom.dll`, et ne le refait
 * **pas** parce qu'un `.cs` a changé de date : il charge l'assemblage existant. Déposer des
 * sources ne suffit donc pas, et redémarrer la plateforme non plus — il faut compiler depuis
 * l'éditeur NinjaScript.
 *
 * Comparer les dates est la seule mesure possible depuis l'extérieur, et elle suffit : une
 * compilation, quelle qu'en soit la raison, embarque forcément nos sources, puisqu'elle est tout
 * ou rien. Un assemblage postérieur à la dernière source déposée est donc à jour.
 *
 * Assemblage absent : rien n'a jamais été compilé sur ce poste, ce qui se traite exactement
 * pareil — il faut compiler.
 */
function compilationAJour(dossierAddOn: string): boolean {
  const assemblage = join(racine, 'NinjaTrader.Custom.dll');
  if (!existsSync(assemblage)) return false;

  try {
    const compileA = statSync(assemblage).mtimeMs;
    const sources = readdirSync(dossierAddOn)
      .filter((n) => n.toLowerCase().endsWith('.cs'))
      .map((n) => statSync(join(dossierAddOn, n)).mtimeMs);
    sources.push(statSync(join(racine, 'Indicators', FICHIER_INDICATEUR)).mtimeMs);

    return compileA >= Math.max(...sources);
  } catch {
    // Un disque qui refuse de répondre ne doit pas faire crier à la péremption : dans le doute,
    // on ne dérange pas.
    return true;
  }
}

/**
 * L'état du dépôt, tel qu'il part dans le battement.
 *
 * Le constat est fait même quand l'add-on répond. Il répondait aussi sur le poste qui tournait
 * six versions en retard : « il parle donc tout va bien » est précisément le raccourci qui
 * rendait la péremption invisible. Deux `statSync` par minute au plus, le coût est nul.
 */
export function etatAddOn(): EtatAddOn {
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
export function journaliserEtat(): void {
  const etat = etatAddOn();
  const details = { dossier: racine, origine: origineRacine };

  switch (etat) {
    case 'DEPLOYED':
      log.event('NinjaTrader', 'Add-on déposé et compilé', details);
      break;
    case 'STALE':
      log.eventWarn('NinjaTrader',
        'Add-on déposé mais non compilé — NinjaTrader exécute une version antérieure ou rien du tout '
        + '(éditeur NinjaScript, F5)', details);
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
