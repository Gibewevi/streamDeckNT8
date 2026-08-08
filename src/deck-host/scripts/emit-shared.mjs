/**
 * Émet vers Bitlearn les modules dont les deux dépôts ont besoin.
 *
 * Ce dépôt est la source ; Bitlearn en reçoit une copie générée. Le point qui fait tout tenir :
 * ce script est branché dans `"build"`, et `npm run build` est le seul chemin vers `dist/host.js`,
 * donc vers l'exécutable installé chez le trader. **On ne peut pas expédier une modification du
 * moteur sans que les copies de Bitlearn soient réécrites.** La divergence n'est plus seulement
 * détectable, elle devient impossible à embarquer.
 *
 *   node scripts/emit-shared.mjs            écrit
 *   node scripts/emit-shared.mjs --check    ne touche à rien, sort non-zéro en cas d'écart
 *
 * La cible se règle par `BITLEARN_PATH`, sinon on suppose les deux dépôts côte à côte.
 */

import { createHash } from 'node:crypto';
import { existsSync, mkdirSync, readFileSync, readdirSync, unlinkSync, writeFileSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const ICI = dirname(fileURLToPath(import.meta.url));
const SOURCE = resolve(ICI, '..', 'src');

/**
 * Les modules partagés. Tout ce qui est ici doit tourner **dans un navigateur** : c'est l'invariant
 * que `--check` fait respecter plus bas. Ce qui dépend de Node vit à côté (`render-node.ts`,
 * `layout.ts`), et n'est jamais copié.
 */
const PARTAGES = [
  'catalog.ts',
  'messages.ts',
  'status-display.ts',
  'visuals.ts',
  'visual-engine.ts',
  'layout-model.ts',
];

/** Ce qui trahit une dépendance à Node dans un fichier censé tourner côté navigateur. */
const INTERDITS = [
  { motif: /from\s+['"]node:[^'"]+['"]/, quoi: "import 'node:*'" },
  { motif: /from\s+['"](fs|path|os|crypto|child_process)['"]/, quoi: 'import de module Node' },
  { motif: /\bBuffer\s*\./, quoi: 'usage de Buffer' },
  { motif: /\bprocess\.(env|cwd)\b/, quoi: 'usage de process' },
];

const cible = () => {
  const racine = process.env.BITLEARN_PATH ?? resolve(ICI, '..', '..', '..', '..', 'Bitlearn');
  return join(racine, 'lib', 'tradeDeck', 'deck-core');
};

/**
 * La seule transformation appliquée au code.
 *
 * L'hôte compile en `moduleResolution: Node16`, qui **exige** l'extension dans les imports
 * relatifs ; Bitlearn est en `moduleResolution: bundler`, qui ne la veut pas. Réécrire ici évite
 * d'imposer à Bitlearn un drapeau `experimental.extensionAlias` côté Next **et** un
 * `moduleNameMapper` côté Jest, pour un besoin qui tient en une ligne.
 */
const transformer = (code) => code.replace(/(from\s+['"]\.\.?\/[^'"]+)\.js(['"])/g, '$1$2');

const sha = (texte) => createHash('sha256').update(texte).digest('hex').slice(0, 16);

/**
 * Le rendu est une fonction pure du fichier source — l'empreinte porte sur la source, pas sur un
 * numéro de commit. Deux exécutions sur le même contenu produisent deux fichiers identiques, ce qui
 * permet à `--check` de comparer octet par octet sans rien ignorer.
 */
function rendre(nom, source) {
  return `/* eslint-disable */
/**
 * ⚠ FICHIER GÉNÉRÉ — NE PAS ÉDITER.
 *
 * Source : dépôt TradeDeck, src/deck-host/src/${nom}
 * Empreinte de la source : ${sha(source)}
 *
 * Modifier là-bas, puis \`npm run build\` dans src/deck-host — ce fichier sera réécrit.
 * Toute retouche faite ici sera perdue au prochain build, sans avertissement.
 */
${transformer(source)}`;
}

function verifierIsomorphie(nom, source) {
  const fautes = [];
  // Les commentaires sont retirés d'abord : « `Buffer` n'existe pas dans un navigateur » est une
  // explication, pas une dépendance.
  const code = source.replace(/\/\*[\s\S]*?\*\//g, '').replace(/\/\/.*$/gm, '');
  for (const { motif, quoi } of INTERDITS) {
    if (motif.test(code)) fautes.push(`${nom} : ${quoi}`);
  }
  return fautes;
}

/**
 * La version est déclarée à deux endroits qui ne peuvent pas se lire l'un l'autre :
 * `package.json`, dont l'installateur tire son numéro, et `host.ts`, qui l'écrit dans l'en-tête de
 * log et la transmet à Bitlearn comme `appVersion`.
 *
 * Un écart entre les deux produit exactement le pire diagnostic possible : un poste dont le journal
 * annonce une version, l'installateur une autre, et Bitlearn une troisième. Vérifié ici plutôt que
 * dans le script d'empaquetage, parce que `npm run build` passe toujours par là.
 */
function verifierVersion() {
  const paquet = JSON.parse(readFileSync(resolve(ICI, '..', 'package.json'), 'utf8')).version;
  const host = readFileSync(join(SOURCE, 'host.ts'), 'utf8').match(/const VERSION = '([^']+)'/)?.[1];

  if (paquet !== host) {
    console.error(`\nVersion incohérente : package.json dit ${paquet}, host.ts dit ${host ?? '(introuvable)'}.`);
    console.error('Les deux doivent porter le même numéro — c\'est celui que lit l\'installateur.');
    process.exit(1);
  }
  return paquet;
}

const version = verifierVersion();
const check = process.argv.includes('--check');
const dossier = cible();
let ecarts = 0;
let fautes = [];

const rendus = new Map();
for (const nom of PARTAGES) {
  const source = readFileSync(join(SOURCE, nom), 'utf8');
  fautes = fautes.concat(verifierIsomorphie(nom, source));
  rendus.set(nom.replace(/\.ts$/, '.ts'), rendre(nom, source));
}

if (fautes.length > 0) {
  console.error('\nCes fichiers partagés dépendent de Node — ils ne peuvent pas tourner dans un navigateur :');
  for (const f of fautes) console.error(`  - ${f}`);
  console.error('\nDéplacer le code fautif hors de l\'ensemble partagé (voir render-node.ts).');
  process.exit(1);
}

if (!existsSync(dossier)) {
  if (check) {
    console.error(`Dossier cible absent : ${dossier}`);
    process.exit(1);
  }
  // Bitlearn peut ne pas être là — machine de build, CI, checkout partiel. On le dit fort et on
  // laisse le build de l'hôte se terminer : l'hôte n'a pas besoin de Bitlearn pour fonctionner.
  try {
    mkdirSync(dossier, { recursive: true });
  } catch (error) {
    console.warn(`\n[emit-shared] Bitlearn introuvable (${dossier}) — copies non mises à jour.`);
    console.warn(`[emit-shared] ${error.message}`);
    process.exit(0);
  }
}

for (const [nom, contenu] of rendus) {
  const chemin = join(dossier, nom);
  const actuel = existsSync(chemin) ? readFileSync(chemin, 'utf8') : null;
  if (actuel === contenu) continue;

  ecarts += 1;
  if (check) console.error(`  dérive : ${nom}`);
  else writeFileSync(chemin, contenu, 'utf8');
}

// Un fichier retiré de l'ensemble partagé doit disparaître de la cible, sinon Bitlearn continuerait
// d'importer une version figée pour toujours — le pire des deux mondes.
for (const present of existsSync(dossier) ? readdirSync(dossier) : []) {
  if (rendus.has(present)) continue;
  ecarts += 1;
  if (check) console.error(`  en trop : ${present}`);
  else unlinkSync(join(dossier, present));
}

if (check && ecarts > 0) {
  console.error(`\n${ecarts} fichier(s) dérivé(s). Lancer « npm run build » puis commiter.`);
  process.exit(1);
}

console.log(check
  ? `[emit-shared] v${version} — ${rendus.size} fichiers partagés à jour.`
  : `[emit-shared] v${version} — ${rendus.size} fichiers émis vers ${dossier}${ecarts ? ` (${ecarts} mis à jour)` : ' (aucun changement)'}`);
