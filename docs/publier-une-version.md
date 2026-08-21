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
et n'offre rien à régler — deux méthodes à expliquer pour une qui répond. · `0.12.0` la Tendance devient armable : un maintien de 1,5 s sur la touche, et les entrées à contre-sens sont refusées — clôturer et réduire restent toujours possibles. Optionnel, désactivé par défaut. Corrige au passage le défaut qui la faisait se périmer deux minutes après chaque chargement (`BarsBack` maintient une fenêtre glissante, donc `Bars.Count` ne bouge jamais). · `0.13.0` sanctions et journal fidèles à ce qui se passe réellement · `0.14.0` **le journal part scellé.** Chaque ligne des trois spools porte un compteur et un HMAC chaîné, ce qui rend détectables l'édition, la suppression, l'insertion et le réordonnancement d'un spool — c'est la condition pour que Bitlearn accorde de l'XP à une séance sans que l'XP se fabrique au bloc-notes. Partent aussi le jour de bourse sur chaque ligne, les limites en vigueur sur `guard.armed`, et un troisième spool d'échantillons de solde. Un poste appairé avant cette version reçoit sa clé au battement, une seule fois. · `0.15.0` **l'installateur vise le serveur pour lequel il a été construit.** Il écrit `bitlearn.json`, que l'hôte et le raccourci lisaient déjà sans que rien ne l'écrive jamais : un client servi par dev.bitlearn.fr installait un poste qui ouvrait bitlearn.fr, où les pages TradeDeck n'existent pas — 404, sans rien à l'écran pour relier la panne à l'adresse. · `0.16.0` **le bridge part autonome.** Publié dépendant du framework, il exigeait un runtime .NET 8 absent d'un Windows neuf : l'exe se terminait aussitôt, lancé en `windowsHide` avec les sorties ignorées, donc sans le moindre message. Le client voyait Bridge et NinjaTrader rouges — le second parce que son état transite par le premier — et rien nulle part pour l'expliquer. L'installateur pese ~30 Mo de plus ; c'est le prix d'une machine sans prérequis. · `0.17.0` **l'installateur pose l'intégration NinjaTrader.** Les seize sources de l'add-on partent dans `AddOns\StreamDeck\`, `TdSwingEngine.cs` dans `Indicators\`, et le client n'a plus rien à faire d'autre que lancer le `.exe`. Jusque-là le voyant NinjaTrader ne pouvait pas passer au vert : rien ne déposait ces fichiers, et la procédure était celle d'un développeur. · `0.18.0` **le voyant NinjaTrader dit pourquoi il est rouge.** L'hôte constate au démarrage l'état du dépôt sur le disque — `NT_MISSING`, `NOT_DEPLOYED`, `INCOMPLETE`, `DEPLOYED` — le journalise et le remonte à Bitlearn, qui le range dans `tradedeck_devices.nt_addon` et l'affiche sous le voyant. « Hors ligne » recouvrait trois pannes distinctes ; comme l'add-on absent ne journalise rien par définition, chacune coûtait un aller-retour avec le client. Le dossier Documents est résolu par la base de registre, comme l'installateur, pour ne pas se tromper de dossier sur un poste où OneDrive l'a repris. · `0.19.0` **on demande à NinjaTrader où il range son NinjaScript, au lieu de le déduire.** La plateforme publie `PERSONAL_ROOT_BIN_CUSTOM` dans `HKCU\Software\NinjaTrader, LLC` ; l'installateur et l'hôte lisent cette valeur, et ne retombent sur `Documents\NinjaTrader 8` que lorsqu'elle manque — plateforme installée mais jamais lancée. `Documents` n'est que l'emplacement par défaut : le déduire faisait dépendre le dépôt d'une convention que rien ne garantit, et un dossier manqué aurait posé l'add-on à côté de là où NinjaScript compile. · `0.20.0` **les six correctifs de l'audit de l'installateur.** Le démarrage automatique d'Elgato est enfin neutralisé — le nettoyage cherchait `Elgato Stream Deck` et `StreamDeck`, la valeur s'appelle `Stream Deck`, et il n'avait donc jamais rien retiré ; la fin d'installation sonde le port 8220 et dit où est le journal quand l'hôte n'a pas démarré ; `ArchitecturesAllowed` et `MinVersion` refusent explicitement un Windows 32 bits ou antérieur à 10, au lieu d'y réussir une installation qui ne démarrera jamais ; la rétrogradation est bloquée pour de vrai — `AppMutex` désignait un mutex que l'application ne crée pas, donc rien n'empêchait un 0.15.0 d'écraser un 0.19.0 ; les sources NinjaScript sont purgées APRÈS la copie, une installation interrompue ne laissant plus NinjaTrader sans intégration ; et le jeton d'appareil quitte le profil itinérant, où il suivait l'utilisateur d'une machine à l'autre. · `0.21.0` **déposer l'add-on ne suffit pas, il faut le compiler — et on le disait mal.** NinjaTrader charge son `NinjaTrader.Custom.dll` déjà compilé et ne refait rien parce qu'un `.cs` a changé sur le disque : « redémarrez NinjaTrader », que répétaient l'installateur, l'éditeur et la documentation, était le seul conseil qui ne pouvait pas marcher. Un client l'a suivi plusieurs fois avant qu'on ne s'en aperçoive. Le geste est *Control Center → New → NinjaScript Editor → F5*. Constaté le 20/08/2026 : le DLL du 12/08 ne contenait pas `JournalSeal`, arrivé dans les sources le 13/08, alors que l'add-on tournait — il tournait sur la compilation du 12. · `0.22.0` **l'hôte détecte une compilation périmée.** Il compare la date de `NinjaTrader.Custom.dll` à celle des sources déposées et remonte `STALE` quand l'assemblage est le plus ancien — y compris quand l'add-on répond, ce qui est justement le cas dangereux : voyant vert, ordres qui passent, et NinjaTrader qui exécute une version antérieure. Six versions ont été livrées à un poste dans cet état sans que rien ne le signale. L'éditeur affiche alors « ok · à recompiler ». · `0.23.0` **installer NinjaTrader ouvert suffit.** Il surveille `bin\Custom` tant qu'il tourne : les sources arrivent, il recompile et recharge l'add-on sans même redémarrer — même PID avant et après, vérifié. Fermé, il ne les verra jamais arriver. Le message de fin dit donc l'un ou l'autre selon ce qu'il constate, et conseille d'ouvrir NinjaTrader avant l'installateur la prochaine fois. · `0.24.0` **macro Auto TP/SL.** Armée, chaque position ouverte reçoit immédiatement son take profit et son stop loss, calculés en ticks depuis le prix moyen et dans le sens de la position ; `0` sur un champ désactive cette jambe. Les deux partent liées en OCO — sans ce lien, un take profit exécuté laisserait le stop actif sur une position à plat, où il n'est plus une protection mais une entrée dans le sens inverse. Les niveaux sont lus APRÈS l'exécution et non à l'envoi de l'ordre : `Account.Submit` rend la main avant le fill, et lire le prix moyen est aussi ce qui fait suivre les protections à chaque renfort. Elle n'ajoute jamais une seconde protection là où le trader en a déjà une, et refuse de s'armer tant que les deux distances valent 0. · `0.25.0` **copie de comptes.** Les ordres du compte sélectionné sont recopiés vers jusqu'à huit comptes suiveurs, chacun avec son multiplicateur et son plafond de contrats — le tout greffé sur la touche Compte plutôt que sur une seconde macro, parce que le compte maître EST le compte sélectionné et qu'un second endroit où le choisir aurait été un second endroit où il peut diverger. Les suiveurs sortent du défilement de la touche : sans ça un appui suffisait à promouvoir un suiveur au rang de maître, qui se copiait alors vers ses propres pairs. Le moteur est un portage de REPEATER9000, dont les mécanismes de fond sont repris tels quels — file d'envoi par suiveur, convergence vers une cible plutôt que rejeu d'événements, OCO reconstruit côté suiveur pour que le courtier tienne le bracket même si le copieur décroche. Quatre de ses défauts sont corrigés au passage, dont celui qui comptait le plus : il ne découvrait les comptes qu'une fois, à l'ouverture de sa fenêtre, si bien qu'un compte reconnecté en séance n'était plus jamais copié pendant que l'écran le montrait toujours configuré. La résolution se refait ici deux fois par seconde. **La copie surveille la dérive et ne la répare jamais** : quand la position d'un suiveur cesse de correspondre à ce que celle du maître implique — rejet pour marge, fill partiel d'un seul côté, fermeture à la main — les entrées cessent d'être copiées vers ce compte, les sorties continuent, et rien n'est envoyé pour corriger. Un système qui rattrape automatiquement un écart qu'il a mal mesuré envoie des ordres marché non sollicités sur un compte réel. Guard garde le dernier mot : la copie des entrées s'arrête avec les entrées, celle des sorties jamais — on n'enferme pas un suiveur dans une position. · `0.25.1` **les comptes suiveurs se cochent, ils ne se tapent plus.** L'interface de configuration proposait d'ajouter une ligne puis de choisir un compte ; elle liste maintenant les comptes que NinjaTrader publie, à cocher. Taper un nom de compte à la main est la façon la plus simple de configurer une copie qui ne partira jamais : une faute de frappe donne un suiveur qui ne résout pas, et le seul signe en est une pastille sur une touche du boîtier, là où personne ne la cherche. Les en-têtes « × » et « Plaf. » portent leur nom entier — Multiplicateur, Plafond (contrats) — et n'apparaissent qu'une fois le compte activé : deux abréviations dans un en-tête de colonne ne disent rien à qui n'a pas écrit la fonctionnalité, et un réglage de dimensionnement mal lu se paie sur un compte réel. Le compte maître s'affiche en tête du tiroir, sous son vrai nom : il n'existe pas de réglage pour lui, et ne le montrer nulle part obligeait à le déduire du texte de substitution « Sim101 / Sim102 », qui ressemble à un réglage sans en être un. · `0.26.0` **changer de compte échange les rôles.** Le réglage décrit désormais un GROUPE de comptes, maître compris ; les suiveurs effectifs s'en déduisent au moment d'envoyer — groupe moins compte sélectionné. Passer de A à B fait sortir B des suiveurs et y fait entrer A, sans que rien d'autre ne bouge et **sans réécrire une ligne du layout**. Il le fallait : `PUT /api/tradedeck/layout` exige une session utilisateur, le poste ne peut pas modifier le layout côté Bitlearn, et une bascule qui l'aurait fait localement aurait divergé du site en silence jusqu'à la première édition qui l'aurait écrasée. La retenue qui suspendait la copie à chaque changement de maître disparaît : elle faisait de la bascule un accident alors qu'elle en est le geste normal, et le risque qu'elle couvrait — un compte lié détenant encore une position de l'ancien maître — est précisément celui que le contrôle de dérive détecte, compte par compte et chiffré. Les comptes du groupe cessent aussi d'être exclus du défilement, puisque en sélectionner un est justement ce qui échange les rôles. Le tiroir est épuré au passage : la liste « Comptes (un par ligne) » disparaît — elle obligeait à retaper des noms que NinjaTrader publie déjà, et son exemple « Sim101 / Sim102 » était le seul nom de compte visible, qu'on prenait pour le compte en service. · `0.26.1` **une case, un nom, rien d'autre.** Le multiplicateur et le plafond de contrats quittent l'écran : chaque compte lié reçoit exactement la quantité du maître, et délier un compte se fait en le décochant. Le tiroir est une colonne étroite, où chaque pixel vertical dépensé est un compte de moins visible sans défiler. Le moteur, lui, sait toujours dimensionner par compte — le code reste pour le jour où le réglage reviendra, et sa valeur par défaut devra alors être la quantité du maître. Mais comme plus aucun contrôle ne le règle, l'hôte le normalise avant d'envoyer et journalise ce qu'il ignore : une valeur héritée d'un layout ancien doublerait sinon une taille sans que rien à l'écran ne le dise, ce qui est le réglage invisible qui agit — le pire mode de défaillance de ce projet. · `0.26.2` **le compte maître ne figure plus dans les comptes liés**, et la ligne se répare. Il y apparaissait depuis la 0.26.0, marqué « maître » : on ne se copie pas vers soi-même, et le montrer ne pouvait que semer le doute. Il reste membre du groupe en mémoire — c'est ce qui le fait redevenir compte lié au changement de compte — mais cette appartenance n'agit jamais en silence : tant qu'il est maître elle ne produit aucune copie, et à l'instant où elle en produirait, il est redevenu visible et coché. La ligne, elle, s'affichait en deux hauteurs, case au-dessus du nom, la pastille collée au nom : `.root label { display: block }` est plus spécifique (0,1,1) que `.flCompte` (0,1,0) et écrasait le `display: flex` de la ligne. Le sélecteur passe à `.root .flCompte`. · `0.26.3` **le maître entre dans le groupe dès qu'on touche la liste**, et non plus seulement à l'allumage de la copie. Une disposition configurée avant que le groupe n'existe ne voyait jamais son maître y entrer : la bascule ne rendait alors rien, et c'est l'état dans lequel se trouvait le poste de test. Sa ligne étant invisible, aucun geste ne permet de l'en retirer — il n'y a donc aucune intention à respecter, et copier depuis un compte, c'est en faire un membre du groupe. Si l'envie de l'en sortir vient un jour, ce sera quand il ne sera plus maître : sa ligne sera alors visible et décochable. · `0.26.4` **audit de la copie : la configuration ne partait pas à la reconnexion.** `syncConfig` part dès que le bridge se connecte, or l'état venait d'être remis à zéro : le compte sélectionné était inconnu, il n'était donc pas soustrait du groupe, et la liste partait entière — maître compris. Le bridge refusait TOUTE la configuration en `COPIER_MASTER_IS_FOLLOWER` puis continuait sur celle qu'il avait persistée, éventuellement calculée pour un autre compte, sans que rien sur le deck ne le montre. Reproduit sur un bridge isolé avant correction. L'hôte ne pousse désormais plus rien tant que le compte est inconnu, et republie dès que la première publication d'état le lui apprend — ce qui rattrape aussi les changements de compte décidés par NinjaTrader lui-même, qui ne passent par aucun appui de touche. · `0.27.0` **on peut enfin comparer un compte lié à son maître.** Le journal ne disait que les décisions — copie partie, copie refusée, dérive — jamais ce que les comptes copiés avaient réellement exécuté : `ExecutionRecorder` n'était abonné qu'au compte suivi. Il l'est désormais au maître et à chaque compte lié, donc prix, quantité, commission et P&L des comptes copiés entrent dans le journal scellé. Le recouvrement avec `OrderMonitor` est voulu et c'est l'enregistreur qui dédoublonne, par identifiant d'exécution : sans ce garde-fou chaque exécution du compte suivi serait écrite deux fois et Bitlearn publierait un P&L double. Chaque copie porte l'identifiant de l'ordre maître dont elle vient, écrit à l'envoi et hors des cartes de liens — celles-ci se vident dès qu'un ordre devient terminal, et l'exécution peut arriver après. Enfin, le prix moyen du maître et l'instant de son exécution sont retenus au fill, ce qui donne les deux chiffres que personne ne pouvait obtenir jusqu'ici : la latence de la copie et son glissement en ticks, journalisés et estampillés sur la ligne d'exécution. Le glissement est signé pour que positif veuille toujours dire « défavorable au compte lié », quel que soit le sens — payer plus cher à l'achat et encaisser moins à la vente sont la même infortune, et les laisser se compenser dans une moyenne aurait rendu la mesure inutile. · `0.28.0` **le compte sélectionné survit au redémarrage, et l'enforcement cesse de ne regarder qu'un compte.** Le fichier de session ne persistait que l'instrument : après un redémarrage l'add-on repartait sur le premier compte que NinjaTrader liste, et deux règles de sécurité suivaient ce défaut en silence — `GuardEnforcer` n'inspecte que les comptes auxquels il est abonné, et le **maître du copieur EST le compte sélectionné**. Le 21/08/2026 un redémarrage a donc pointé l'enforcement sur un compte que personne ne tradait et interverti le maître avec son suiveur, pendant une heure d'ordres passés à la souris, le boîtier affichant `SAFETY:MAX` en rouge et le journal ne portant pas une seule ligne d'annulation. `session.json` porte désormais `instrument` **et** `account`, et le bridge pousse `setAccount` avant `setInstrument` à chaque connexion de l'add-on ; sans compte enregistré il le journalise au lieu de se taire. L'add-on, lui, crie en `SECURITY` s'il adopte une politique bloquante sans qu'on lui ait jamais nommé de compte — un verrou qui surveille le mauvais compte est pire qu'un verrou absent, il rassure. **L'enforcement couvre maintenant tout le groupe de copie** : le moteur de copie est déjà abonné au maître et à chaque suiveur résolu, il inspecte donc leurs ordres avant de copier, ce qui referme le trou par lequel un compte lié restait joignable à la souris sans qu'aucune règle ne le regarde. Un compte hors du groupe reste hors de portée : c'est le périmètre que le deck gouverne, l'étendre à toute la plateforme annulerait des ordres sur des comptes jamais liés. Enfin **le plafond de contrats porte sur la position, plus sur l'ordre** : il ne clampait qu'une quantité isolée, si bien que deux entrées d'un contrat sous un plafond d'un passaient toutes les deux et laissaient le suiveur à deux. Une copie d'entrée est désormais rabotée à la place restante, ou refusée, contre la position qu'elle produirait — le plafond le plus serré entre celui du suiveur et celui de la macro. Il le fallait ici et pas ailleurs : une copie est volontairement exemptée de l'annulation de `GuardEnforcer`, pour ne pas appliquer deux fois et asymétriquement la règle déjà passée sur le maître, donc le plafond l'atteint à la soumission ou jamais. Le contrôle de dérive, qui tenait tout écart de taille pour légitime dès qu'un plafond existait, excusait du même coup un suiveur AU-DESSUS de ce plafond — le seul état que le plafond existe pour empêcher, et le seul qui ne pouvait jamais être signalé. Il le voit, sous son propre nom : `OVER CAP` au journal, `PLAFOND` sur la touche, pour ne pas envoyer chercher une divergence qui n'existe pas.

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

# 3. LE BRIDGE, EN RELEASE ET AUTONOME  ← voir le piège ci-dessous
npm run build:bridge

# 4. L'hôte (régénère aussi deck-core/ côté Bitlearn)
npm run build

# 5. L'installateur
powershell -ExecutionPolicy Bypass -File packaging/build-installer.ps1

# 6. Publier — l'ancien est SUPPRIMÉ, la route sert le plus récent et
#    laisser les deux serait un piège
rm -f ../../../Bitlearn/private/tradedeck/BitlearnTradeDeck-Setup-*.exe
cp ../../build/BitlearnTradeDeck-Setup-0.8.0.exe ../../../Bitlearn/private/tradedeck/
```

## Un paquet pour dev.bitlearn.fr

```bash
powershell -ExecutionPolicy Bypass -File packaging/build-installer.ps1 -BitlearnUrl https://dev.bitlearn.fr
# → build/BitlearnTradeDeck-Setup-0.15.0-dev.exe
```

Le serveur visé traverse tout le paquet : l'installateur écrit
`%APPDATA%\StreamDeckTrader\bitlearn.json`, que l'hôte **et** le raccourci du bureau lisent, et le
repli du lanceur est réécrit à la construction. Le suffixe `-dev` du nom de fichier n'est pas
cosmétique : deux `.exe` de même version pointant deux serveurs sont indiscernables une fois
téléchargés, et se tromper des deux mène à un 404 sans cause visible.

Ce paquet se dépose dans le dossier de l'environnement **dev**, jamais dans celui de production :

```bash
EXE=BitlearnTradeDeck-Setup-0.15.0-dev.exe
VPS=debian@vps-a7e6d37c.vps.ovh.ca
DEST=/home/bitlearn/bitlearn_dev/private/tradedeck

scp -P 50000 build/$EXE $VPS:/tmp/
ssh -p 50000 $VPS "sudo -u bitlearn bash -c 'rm -f $DEST/*.exe && mv /tmp/$EXE $DEST/'"
```

**Changer de serveur désapparie le poste.** Le jeton d'appareil ne vaut que pour le serveur qui l'a
émis, et l'hôte se considère appairé tant qu'il existe : le garder produirait un poste
silencieusement désynchronisé — 401 à chaque appel, aucune nouvelle demande d'appairage, et le deck
qui continue de trader comme si de rien n'était. L'installateur l'efface donc quand la cible change,
et le navigateur redemande une autorisation au démarrage suivant. Un fichier `bitlearn.json` absent
compte pour la production : sans cette équivalence, la première mise à jour vers 0.15.0
désapparierait tous les postes déjà installés.

## Le piège : Release contre Debug, et dépendant contre autonome

**`dotnet build` compile en Debug. L'installateur consomme la sortie *Release*.** Et
`dotnet publish` sans `--self-contained` produit un bridge qui exige un runtime .NET 8 sur la
machine du client — runtime que Windows n'embarque pas. `npm run build:bridge` porte les deux
réglages ; l'appeler à la main sans eux produit un paquet qui s'installe, démarre, et laisse
deux voyants rouges sans explication. `build-installer.ps1` refuse désormais d'empaqueter un
bridge sans `hostfxr.dll`, le marqueur d'une publication autonome.

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
console.log("autonome:", fs.existsSync("build/payload/bridge/hostfxr.dll"));
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
