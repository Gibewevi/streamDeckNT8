# Publier une version de TradeDeck

Procédure pour produire un installateur et le rendre téléchargeable depuis Bitlearn.

## Quand

**Dès qu'une modification touche `src/deck-host`, `src/StreamDeckBridge` ou
`src/NinjaTrader.AddOn.StreamDeck`.** Sans attendre qu'on le demande.

La raison n'est pas l'hygiène : Bitlearn se déploie d'un `git pull`, mais l'hôte est un `.exe`
installé sur le poste du trader. Tant qu'il n'est pas reconstruit, l'éditeur Bitlearn propose des
réglages que le boîtier ne sait pas appliquer. C'est le pire état possible — l'écran promet une
protection qui n'existe pas côté moteur, et personne ne le découvre avant qu'elle serve.

Une modification qui ne touche que Bitlearn (pages, journal, statistiques) n'a besoin de rien.

## Numérotation

`MAJEUR.MINEUR.CORRECTIF`, avant 1.0 :

- **MINEUR** — nouvelle macro, nouvelle règle, changement de comportement d'une protection
- **CORRECTIF** — correction sans changement de comportement observable

Historique : `0.1.0` premier installateur · `0.2.0` journalisation, pause obligatoire, liquidation
automatique, partage du code avec Bitlearn · `0.3.0` la pause devient une macro autonome ·
`0.4.0` publication de la cash value du compte · `0.5.0` retrait du mode développement — le verrou
de la macro de sécurité ne peut plus être levé avant son échéance, par aucun chemin ·
`0.5.1` la tâche planifiée s'enregistre enfin (collision `$action`/`$Action` dans
`register-task.ps1`), plus de fenêtre console au démarrage · `0.6.0` la pause obligatoire égrène
ses secondes (`9:58`) au lieu d'un arrondi à la minute qui paraissait figé · `0.7.0` le poste remonte son état vivant : l'éditeur Bitlearn dessine les touches telles que le boîtier les montre · `0.7.1` la quantité suit enfin sur les touches d'entrée, l'armement Auto BE survit aux poussées de disposition · `0.8.0` l'Auto BE refuse un décalage supérieur au seuil, et la cash value cesse d'être perdue par le garde de sélection de compte · `0.9.0` la pause obligatoire impose enfin toute sa durée — elle était ancrée sur le dernier trade et expirait donc quelques secondes après s'être ouverte — et la cash value cesse d'être perdue une seconde fois, omise cette fois du snapshot diffusé au client, ce qui laissait le journal Bitlearn sans capital de départ. · `0.10.0` macro Tendance : la première
qui regarde le marché et non plus la comptabilité de la séance. Elle n'en refuse encore aucun ordre
— elle affiche le sens et journalise ce qu'elle aurait refusé, le temps de calibrer le seuil sur une
vraie séance. · `0.11.0` la Tendance perd son mode Heikin Ashi : une seule méthode, la structure de
marché. La couleur d'une bougie HA est une statistique à une barre, elle bascule à chaque pullback
et n'offre rien à régler — deux méthodes à expliquer pour une qui répond. · `0.12.0` la Tendance devient armable : un maintien de 1,5 s sur la touche, et les entrées à contre-sens sont refusées — clôturer et réduire restent toujours possibles. Optionnel, désactivé par défaut. Corrige au passage le défaut qui la faisait se périmer deux minutes après chaque chargement (`BarsBack` maintient une fenêtre glissante, donc `Bars.Count` ne bouge jamais). · `0.13.0` sanctions et journal fidèles à ce qui se passe réellement · `0.14.0` **le journal part scellé.** Chaque ligne des trois spools porte un compteur et un HMAC chaîné, ce qui rend détectables l'édition, la suppression, l'insertion et le réordonnancement d'un spool — c'est la condition pour que Bitlearn accorde de l'XP à une séance sans que l'XP se fabrique au bloc-notes. Partent aussi le jour de bourse sur chaque ligne, les limites en vigueur sur `guard.armed`, et un troisième spool d'échantillons de solde. Un poste appairé avant cette version reçoit sa clé au battement, une seule fois.

## Où vit le numéro

Quatre endroits, et **`npm run build` refuse de construire s'ils divergent** (voir
`scripts/emit-shared.mjs`) :

| Fichier | Rôle |
|---|---|
| `src/deck-host/package.json` | la source — `build-installer.ps1` lit celle-ci |
| `src/deck-host/package-lock.json` | deux occurrences, à jour sous peine de bruit dans `git diff` |
| `src/deck-host/src/host.ts` | en-tête de log, et `appVersion` transmis à Bitlearn |
| `src/deck-host/packaging/TradeDeck.iss` | repli si l'on invoque ISCC à la main |

## Procédure

```bash
# 1. Le numéro, partout
cd "src/deck-host"
sed -i 's/"version": "0.7.1"/"version": "0.8.0"/' package.json package-lock.json
sed -i "s/^const VERSION = '0.7.1';/const VERSION = '0.8.0';/" src/host.ts
sed -i 's/#define AppVersion "0.7.1"/#define AppVersion "0.8.0"/' packaging/TradeDeck.iss

# 2. Arrêter l'hôte et le bridge — sinon les fichiers sont verrouillés
#    (PowerShell : Stop-Process sur node.exe / StreamDeckBridge.exe / wscript.exe
#     dont la ligne de commande contient TradeDeck ou StreamDeckTrader)

# 3. LE BRIDGE, EN RELEASE  ← voir le piège ci-dessous
dotnet build ../StreamDeckBridge/StreamDeckBridge.csproj -c Release

# 4. L'hôte (régénère aussi deck-core/ côté Bitlearn)
npm run build

# 5. L'installateur
powershell -ExecutionPolicy Bypass -File packaging/build-installer.ps1

# 6. Publier — l'ancien est SUPPRIMÉ, la route sert le plus récent et
#    laisser les deux serait un piège
rm -f ../../../Bitlearn/private/tradedeck/BitlearnTradeDeck-Setup-*.exe
cp ../../build/BitlearnTradeDeck-Setup-0.8.0.exe ../../../Bitlearn/private/tradedeck/
```

## Le piège : Release contre Debug

**`dotnet build` compile en Debug. L'installateur consomme la sortie *Release*.**

Vécu le 07/08/2026 : trois modifications du bridge (pause obligatoire, liquidation automatique)
compilées et vérifiées toute une session — en Debug. La sortie Release datait de la veille. Sans
l'étape 3, l'installateur aurait embarqué un bridge sans aucune de ces règles, et rien ne l'aurait
signalé : le build réussit, l'installateur se construit, il est simplement faux.

Contrôle après coup, à faire systématiquement :

```bash
node -e '
const fs=require("fs");
const t=(f,s)=>{const b=fs.readFileSync(f);
  return b.includes(Buffer.from(s,"utf16le"))||b.includes(Buffer.from(s,"latin1"));};
const b="build/payload/bridge/StreamDeckBridge.dll";
console.log("pause:", t(b,"SAFETY_MANDATORY_PAUSE"), "| liquidation:", t(b,"flattenAccount"));
console.log("version:", fs.readFileSync("build/payload/dist/host.js","utf8")
  .match(/VERSION = .([\d.]+)./)?.[1]);'
```

Chercher le **motif d'octets**, pas avec `strings` : cette commande n'existe pas dans le Git Bash
de cette machine et échoue en silence, `grep` compte alors zéro et l'on conclut « absent » à tort.
Les littéraux .NET sont en UTF-16, un décodage global rate ceux placés à un décalage impair —
d'où la recherche de `Buffer.from(s, "utf16le")` à n'importe quelle position.

## Ce que la publication déclenche côté Bitlearn

Rien à déployer. `tradeDeckReleaseService` lit le dossier à chaque affichage :

- le bouton de `/tradedeck` passe à `Télécharger [v0.8.0]`
- la ligne sous le bouton donne la taille et la date
- `GET /api/tradedeck/download` sert le nouveau fichier, sous habilitation

Déposer le `.exe` suffit. Le vérifier : `npx jest app/server/services/tradeDeckReleaseService`.

## Signature de code — non fait

L'installateur n'est pas signé. SmartScreen affiche « Éditeur inconnu » et cache le bouton
d'exécution derrière « Informations complémentaires ». Sur un produit payant, c'est un frein de
conversion réel.

Un certificat OV coûte 200–400 €/an, la réputation SmartScreen se construisant ensuite sur quelques
centaines de téléchargements ; un EV l'accorde immédiatement, pour environ le double.
`build-installer.ps1` accepte déjà `-SignPfx <chemin.pfx> -SignPassword <motdepasse>`.

## Après installation

Le trader doit relancer TradeDeck. Les réglages sont conservés : le layout vit dans Bitlearn,
l'état de la macro de sécurité dans `%APPDATA%\StreamDeckTrader\safety-macro.json`. L'installateur
arrête lui-même l'hôte et le bridge (`PrepareToInstall`), et le verrou de sécurité survit à la
mise à jour — c'est vérifié, redémarrer n'est pas un moyen de le lever.
