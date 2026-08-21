# Copie de comptes — extension de la macro Compte

Recopie les ordres du **compte sélectionné** vers un ou plusieurs **comptes suiveurs**, à
l'intérieur de NinjaTrader, sans que le chemin maître → suiveur ne traverse jamais le bridge.

> **Ce n'est pas une macro à part.** La sélection de compte existe déjà, sur la touche Compte : la
> copie s'y greffe plutôt que de dupliquer cette logique. Le compte maître **est** le compte
> sélectionné, et les suiveurs sont un réglage de cette même touche.

> Le moteur est un portage de [`REPEATER9000.cs`](https://github.com/nikos-repos/REPEATER9000),
> dont les mécanismes de fond sont bons. Ce document dit **ce qui en a été repris**, **ce qui en a
> été corrigé**, et surtout **ce que cette macro refuse de faire** — la section
> [Dérive](#la-dérive--on-sarrête-on-ne-répare-pas) est la plus importante du fichier.

## Ce que voit le trader

```
Touche Compte
  ┌ COMPTE MAÎTRE ──────────────┐
  │ Sim101                      │   ← affiché, non modifiable : c'est le compte sélectionné
  └─────────────────────────────┘
  ◯ Journaliser ce compte
  ● Copier les positions

  COMPTES LIÉS                       ← n'apparaît que si la copie est activée
   ☑ Sim102
   ☐ Sim103
   ☑ APEX-4471
```

**Le compte maître n'y figure jamais** — on ne se copie pas vers soi-même. On copie donc vers
Sim102 et APEX-4471, chacun recevant **exactement la quantité envoyée sur Sim101** : un contrat sur
le maître, un contrat sur chaque compte lié ; deux sur le maître, deux sur chacun.

Sélectionner Sim102 sur la touche ferait copier vers Sim101 et APEX-4471 : Sim102 quitte la liste
puisqu'il devient maître, Sim101 y apparaît coché. Les rôles s'échangent, rien d'autre ne bouge.

Les comptes sont **proposés, jamais saisis** : la liste est celle que NinjaTrader publie, et il n'y
a qu'à cocher. Taper un nom de compte à la main est la façon la plus simple de configurer une copie
qui ne partira jamais — une faute de frappe donne un compte qui ne résout pas, et le seul signe en
est une pastille sur une touche du boîtier, là où personne ne la cherche.

- recopie des entrées, des sorties, des déplacements de stop/target et des annulations ;
- **chaque compte lié reçoit exactement la quantité du maître** ;
- **OCO reconstruit côté suiveur**, si bien que le courtier tient le bracket même si le copieur
  décroche ;
- **contrôle de dérive qui arrête un suiveur, sans jamais tenter de le corriger.**

## Le maître est le compte sélectionné

Il n'existe pas de réglage « compte maître » : c'est le compte que la touche Compte a sélectionné,
celui sur lequel le deck envoie déjà ses ordres. Deux conséquences, toutes deux voulues.

**Le réglage décrit un GROUPE, pas des suiveurs.** Le maître en est membre — sans jamais être
affiché. Les suiveurs effectifs s'en déduisent au moment d'envoyer : *groupe moins compte
sélectionné*.

Cette appartenance invisible n'agit jamais en silence, et c'est ce qui la rend acceptable : tant
que le compte est maître elle ne produit aucune copie, et à l'instant où elle en produirait, il est
redevenu visible et coché dans la liste.

**Changer de compte échange donc les rôles, tout seul.** Groupe `{A, B, C}`, maître `A` → on copie
vers `B` et `C`. La touche passe à `B` → on copie vers `A` et `C`. Le compte qu'on prend quitte les
suiveurs, celui qu'on quitte y entre, les autres ne bougent pas. **Aucune ligne du layout n'est
réécrite** — et il le fallait : `PUT /api/tradedeck/layout` exige une session utilisateur, le poste
ne peut pas modifier le layout côté Bitlearn. Une bascule qui l'aurait fait localement aurait
divergé du site en silence, jusqu'à la première édition qui l'aurait écrasée sans prévenir.

C'est aussi ce qui supprime le piège : un membre du groupe sélectionné devient maître et cesse
d'être copié au même instant. Il ne peut pas se copier vers lui-même, et le refus
`COPIER_MASTER_IS_FOLLOWER` du bridge n'a plus l'occasion de se déclencher — il reste comme
garde-fou.

> **La copie n'est plus retenue au changement de maître.** Elle l'était, par une retenue qu'il
> fallait lever à la main : la bascule était alors un accident. Elle en est devenue le geste
> normal. Le risque qu'elle couvrait — un compte lié détenant encore une position ouverte par
> l'ancien maître — est exactement celui que le [contrôle de dérive](#la-dérive--on-sarrête-on-ne-répare-pas)
> détecte, et il le fait mieux : compte par compte, chiffré, et il se lève seul quand les positions
> se rejoignent.

## D'où vient le moteur

### Repris tel quel

| Mécanisme | Ce qu'il empêche |
|---|---|
| Un thread et une file **par compte suiveur** | Bloquer le thread d'événements de NinjaTrader. Deux suiveurs lents ne se pénalisent pas l'un l'autre |
| Modèle « converger vers une cible » (`OrderLink.Target*`) | Rejouer des événements dans le désordre. Le maître peut bouger son stop pendant que l'envoi au suiveur est encore en vol |
| Enregistrer **tous** les liens avant tout envoi | Qu'une annulation maître arrivée une milliseconde plus tard ne trouve aucune correspondance |
| Un identifiant OCO suiveur par (OCO maître, suiveur) | Qu'un take profit exécuté laisse le stop actif sur une position à plat — où il n'est plus une protection mais une entrée à l'envers |
| Sortie jugée contre la position **réelle** du maître | De copier un `Sell` passé à plat comme une sortie : NinjaTrader n'utilise pas `SellShort` de façon fiable, c'est une entrée short |
| Pas de sortie vers un suiveur sans exposition copiée | D'envoyer une sortie « nue », qui ouvrirait une position au lieu d'en fermer une |
| Annulations exécutées **hors file**, sur le thread d'événement | Qu'une action de risque attende derrière une file d'envois |

### Corrigé en portant

| Défaut d'origine | Ce qui est fait ici |
|---|---|
| Quantité suiveur = quantité maître, sans réglage possible | Le moteur sait dimensionner par compte ; le réglage n'est pas exposé aujourd'hui (§ [Dimensionnement](#dimensionnement)) |
| Comptes découverts **une seule fois**, à l'ouverture de la fenêtre : un compte reconnecté n'était plus jamais copié, et l'écran le montrait quand même configuré | Résolution refaite à chaque publication d'état, et `resolved: false` remonté par suiveur — un suiveur non résolu se voit |
| Indicateur d'activation `bool` non `volatile`, lu depuis les threads d'événements | Un objet de configuration immuable, échangé atomiquement |
| Rejet d'un ordre suiveur traité comme une annulation : lien supprimé, rien remonté | Événement `copierViolation` → bridge → hôte, touche rouge, ligne de journal |
| Aucune surveillance de l'écart maître / suiveur | § [Dérive](#la-dérive--on-sarrête-on-ne-répare-pas) |
| `Trace.WriteLine` | `SdLogger`, catégorie `Copier`, corrélé comme le reste |
| Threads de travail jamais arrêtés | Arrêtés dans le `Shutdown()` de l'add-on — NinjaScript recharge l'add-on à chaque recompilation |
| Sondes de latence dans un CSV à part | Latence **et** glissement, sur chaque exécution copiée, dans le journal scellé — § [Comparer un compte lié à son maître](#comparer-un-compte-lié-à-son-maître) |

## Où vit le moteur

```
Site Bitlearn ──► layout ──► hôte ──► bridge ──► add-on ──► NinjaTrader
  réglages de                pousse   valide,    CopyEngine
  la touche                  la       persiste,  ▲
  Compte                     config   arbitre    │ Account.OrderUpdate
                                                 └── maître ─► suiveurs
```

**Le chemin maître → suiveur ne quitte jamais NinjaTrader.** Faire transiter chaque mise à jour
d'ordre par le bridge ajouterait des millisecondes sur le chemin critique et un mode de panne de
plus : un pont coupé au mauvais moment laisserait un suiveur avec une entrée et sans son stop.
Le bridge décide **si** la copie a lieu ; il ne participe pas à chaque ordre.

La configuration, elle, suit le chemin inverse et complet : elle est **détenue et persistée par le
bridge**, qui la republie à l'add-on à chaque (re)connexion — exactement comme `setGuardPolicy`.
C'est ce qui fait qu'une recompilation NinjaScript en pleine séance ne perd pas la configuration.

## Dimensionnement

**Chaque compte lié reçoit exactement la quantité du maître.** Un contrat sur le maître, un
contrat sur chaque compte lié ; deux sur le maître, deux sur chacun. Il n'y a rien à régler : pour
délier un compte, on le décoche.

Le moteur sait pourtant dimensionner par compte — multiplicateur et plafond par ordre, arrondi à
l'entier le plus proche, `0` n'envoyant rien plutôt que d'arrondir à 1. Ce code **reste**, pour le
jour où le réglage reviendra, et le format de stockage le porte toujours
(`nom|multiplicateur|plafond`).

Mais **plus aucun contrôle ne le règle**, et l'hôte le normalise donc à `×1` sans plafond avant
d'envoyer, en journalisant ce qu'il a ignoré. Une valeur héritée d'un layout ancien doublerait
sinon une taille sans que rien à l'écran ne le dise — c'est le réglage invisible qui agit, le pire
mode de défaillance de ce projet.

> Si le plafond revient un jour comme garde-fou, **sa valeur par défaut sera la quantité du
> maître** : un compte lié qui se met à recevoir moins que ce qu'on croit est aussi trompeur qu'un
> compte qui reçoit plus.

La limite de position, elle, reste celle de Guard (`maxContracts`), qui s'applique compte par
compte et n'a jamais dépendu de ce réglage.

## La dérive — on s'arrête, on ne répare pas

**C'est la règle qui prime sur tout le reste de ce document.**

Un copieur d'ordres n'est pas un copieur de positions. Un rejet pour marge insuffisante, un fill
partiel d'un seul côté, un redémarrage en pleine position : le maître et le suiveur divergent.
REPEATER9000 ne le remarque jamais.

Ici, à chaque publication d'état, on compare pour chaque couple (suiveur, instrument) :

```
position attendue = position nette du maître        (multiplicateur 1)
position réelle   = position nette du suiveur
```

Deux natures d'écart, jugées différemment :

| Écart | Jugé ? |
|---|---|
| **Sens opposé**, maître à plat et suiveur non plat, ou l'inverse | **Toujours.** C'est le cas dangereux : une exposition que personne n'a voulue |
| **Taille seule**, même sens | Toujours, tant qu'aucun plafond n'est en vigueur — et il n'y en a plus. Un plafond créerait un écart légitime et permanent, dont l'alerte n'apprendrait rien ; la règle reste écrite pour le jour où il reviendrait |

Un écart **persistant au-delà du délai de stabilisation** (aucun ordre copié en vol depuis 3 s
pour ce couple) déclare le suiveur **en dérive**. Alors, et seulement alors :

1. **les nouvelles entrées cessent d'être copiées vers ce suiveur** ;
2. **les sorties continuent de l'être** — un suiveur en dérive détient possiblement une position,
   et fermer doit toujours rester possible ;
3. l'événement remonte : `copierViolation`, touche rouge, `WARN` dans le journal, écart chiffré ;
4. l'état de dérive **se lève tout seul quand l'écart redevient nul**, et jamais autrement.

**Aucun ordre n'est émis pour corriger une dérive.** Ni ordre marché de rattrapage, ni
liquidation « pour repartir propre ». Un système qui corrige automatiquement une divergence qu'il
a mal mesurée envoie des ordres marché non sollicités sur un compte réel — c'est la façon dont un
copieur vide un compte, et c'est exactement ce que cette macro refuse de faire. Le rôle du copieur
s'arrête à **constater, s'arrêter, et le dire**. La correction est un geste de trader.

> **Activer la copie alors qu'une position est déjà ouverte déclenche une dérive, et c'est
> correct.** Le suiveur est à plat, le maître non : l'écart est réel. Un copieur ne peut pas
> reproduire une position qu'il n'a pas ouverte, et prétendre le contraire supposerait justement
> l'ordre de rattrapage que l'on refuse d'émettre. La dérive se lève d'elle-même dès que le maître
> revient à plat. C'est la surprise la plus probable au premier essai : activer la copie **hors
> position**.

## L'arbitrage avec Guard

La macro de sécurité vit dans le bridge et ne connaît que le compte sélectionné. Un copieur
multiplie l'exposition par le nombre de suiveurs : sans arbitrage explicite, il serait un
contournement de toutes les limites.

- **le bridge refuse d'activer la copie** tant que `entriesBlocked` est vrai ;
- quand Guard se met à bloquer en cours de séance (perte du jour, limite de trades, pause,
  liquidation automatique), l'état est poussé au copieur : **les entrées cessent d'être copiées,
  les sorties continuent**. On n'enferme jamais un suiveur dans une position — c'est déjà la règle
  écrite dans `GuardEnforcer`, et elle vaut ici mot pour mot ;
- les ordres copiés portent `Name = "StreamDeckCopy"` et `GuardEnforcer` les reconnaît **par
  identifiant d'ordre réellement créé par le moteur**, jamais par le nom seul : un nom se tape à
  la main dans un DOM, un identifiant non ;
- `allowLiveAccounts` est évalué sur **chaque suiveur**, à la configuration comme à l'activation ;
- **huit suiveurs au maximum.** Chaque suiveur supplémentaire multiplie l'exposition et allonge la
  file d'envoi.

## La touche Compte

La touche garde son rôle : elle affiche le compte et le fait défiler. La copie s'ajoute en
sous-titre, sans jamais masquer l'identité du compte.

| État | Aspect |
|---|---|
| Copie éteinte | `ACC-101` / `ACTIVE` — inchangé |
| Copie active, tout résolu | `ACC-101` / `COPY ×3`, orange |
| Un suiveur non résolu | `ACC-101` / `COPY 2/3`, badge blanc |
| Guard bloque les entrées | `ACC-101` / `COPY ×3`, détail `SORTIES`, orange atténué |
| Dérive ou rejet sur un compte lié | `ACC-101` / `COPY STOP`, détail `DERIVE` ou `REJET`, rouge |

Appui : défilement du compte, **groupe compris** — c'est lui qui échange les rôles. La copie ne se
coupe pas depuis le boîtier : c'est un réglage, pas une commande de séance.

Au repos — copie éteinte, ou aucun suiveur — la touche rend **exactement** comme avant : c'est ce
que vérifient les 28 instantanés de `deckPreview.test.js` côté Bitlearn, tous verts sans mise à jour.

## Réglages de la touche Compte

Le tiroir tient en quatre blocs, dans cet ordre : le **compte maître** (affiché, non modifiable —
c'est le compte sélectionné), *Journaliser ce compte*, *Copier les positions*, et la liste des
**comptes liés** qui n'apparaît que si la copie est activée.

| Clé | Type | Rôle |
|---|---|---|
| `journal` | `toggle` | *(existant)* Journaliser ce compte vers Bitlearn |
| `copyEnabled` | `toggle` | Copier les positions vers les comptes liés |
| `followers` | `followerList` | Les comptes du groupe. Case cochée = lié, décochée = délié |

> **Le réglage `accounts` a été retiré** — une liste de comptes à saisir, un par ligne, qui
> filtrait et ordonnait le défilement. Il obligeait à retaper des noms que NinjaTrader publie
> déjà, et son exemple « Sim101 / Sim102 » était le **seul nom de compte visible dans le tiroir** :
> on le prenait pour le compte en service. Le défilement parcourt maintenant les comptes actifs,
> moins ceux qui sont liés.
>
> Une clé résiduelle dans un layout ancien est **ignorée**, par `getAccountCycleList` comme par
> `comptesJournalises`. Un réglage retiré de l'écran qui continuerait d'agir serait le pire des
> deux mondes : un défilement restreint, ou des séances non journalisées, par une liste que plus
> personne ne voit ni ne peut corriger.

> **`followers` est une CHAÎNE, pas un tableau — et ce n'est pas négociable.**
> `sanitizeSettings` (`Bitlearn/lib/tradeDeck/layout.js`) n'accepte que `string`, `boolean` et
> `number` dans les réglages d'une touche : un tableau ou un objet est **silencieusement écarté**
> en traversant le site, et le trader verrait sa sélection disparaître sans un message.

Format : **une ligne par compte lié**, `nom|multiplicateur|plafond`.

```
Sim102|1|0
Sim103|2|5
APEX-4471|1|3
```

Lisible, modifiable à la main, et il survit à la traversée du site. L'éditeur affiche des lignes
avec un sélecteur de compte alimenté par la liste vivante (`availableAccounts`, remontée toutes
les 5 s par le battement) et **retombe en saisie libre quand le poste est hors ligne** : ne pas
pouvoir configurer parce que NinjaTrader est fermé serait un défaut, pas une protection.

## Protocole

### `configureCopier` — hôte → bridge

> **Quand elle part, et pourquoi ça compte.** À la connexion du bridge, à chaque édition du layout,
> et **à chaque changement de compte sélectionné** — y compris quand ce n'est pas la touche qui l'a
> changé : NinjaTrader en choisit un tout seul quand le compte suivi disparaît.
>
> Elle ne part **pas** tant que le compte est inconnu. La liste envoyée est le groupe moins le
> compte sélectionné : sans lui, le groupe partirait entier, maître compris, et le bridge
> refuserait tout en `COPIER_MASTER_IS_FOLLOWER` — pour ensuite continuer sur la configuration
> qu'il avait persistée, donc potentiellement sur une liste calculée pour un autre compte. C'est
> exactement ce qui se produisait à chaque reconnexion avant l'audit du 20/08/2026 : `syncConfig`
> partait à la connexion, alors que l'état venait d'être remis à zéro.

Le maître n'est pas transmis : le bridge le connaît déjà, c'est le compte sélectionné.

```json
{ "type": "command", "action": "configureCopier",
  "payload": { "enabled": true, "followers": "Sim102|1|0\nSim103|2|5" } }
```

### `copierPanic` — hôte → bridge → add-on

Sans charge utile. Désactive la copie **puis** liquide chaque suiveur résolu. C'est le seul endroit
où le moteur envoie des ordres de lui-même, et il faut une commande délibérée pour y arriver —
jamais une mesure.

> **Aucune touche ne produit cette action aujourd'hui.** Elle existe dans le protocole et dans
> l'add-on ; le geste qui la déclenchera reste à choisir. Même statut que `flattenAccount`, dont la
> macro de sécurité est le seul émetteur.

### `setCopierConfig` — bridge → add-on

Poussé à la (re)connexion de l'add-on, puis **uniquement au changement** : la boucle de diffusion
tourne à 5 Hz et un envoi inconditionnel noierait l'add-on et son journal, comme pour
`setGuardPolicy`.

```json
{ "type": "command", "action": "setCopierConfig",
  "payload": { "enabled": true, "master": "Sim101",
               "followers": [ { "name": "Sim102", "multiplier": 1.0, "maxContracts": 0 } ],
               "entriesBlocked": false } }
```

`entriesBlocked` est le report de la décision de Guard. L'add-on ne la recalcule pas : le bridge
est le seul arbitre.

### `copier` dans `stateUpdate` — add-on → bridge → hôte

```json
{ "copier": {
    "enabled": true, "master": "Sim101", "entriesBlocked": false,
    "followers": [
      { "name": "Sim102", "resolved": true, "drifted": false, "drift": 0, "lastError": "" },
      { "name": "Sim103", "resolved": true, "drifted": true, "drift": -2, "lastError": "" }
    ],
    "copiedToday": 14 } }
```

| Champ | Description |
|---|---|
| `resolved` | Le compte existe et sa connexion est active **en ce moment** |
| `drifted` | Écart constaté et stabilisé — les entrées ne sont plus copiées vers ce suiveur |
| `drift` | Écart signé en contrats : `réel − attendu` |
| `lastError` | Dernier refus rencontré sur ce suiveur (rejet, marge), vide sinon |

### `copierViolation` — add-on → bridge → hôte

Même forme que `guardViolation`. Émis sur un rejet de suiveur et sur une entrée en dérive.

## Ce que le journal dit, et ce qu'il ne dit pas

Vingt-cinq lignes de journal couvrent le copieur, catégorie `Copier` dans
`addon-AAAA-MM-JJ.log`. Elles décrivent **les décisions** :

| Ligne | Ce qu'elle permet de vérifier |
|---|---|
| `Routes resolved — master=… followers=Sim101×1` | Quels comptes sont réellement armés. Un `!` devant un nom = non résolu |
| `Copying Buy Market qty=2 on MNQ 09-26 to 2 follower(s)` | Un ordre maître a bien déclenché N copies |
| `Entry not copied to X — account is in drift (…)` | Pourquoi un compte n'a rien reçu |
| `Nothing copied to X — 1 × 0,3 rounds to zero contracts` | Idem, cas du dimensionnement |
| `Entry not copied — the safety macro is blocking entries` | Idem, cas de Guard |
| `COPY REJECTED on X — … That account did NOT take the trade.` | Un compte a refusé l'ordre |
| `DRIFT on X / MNQ 09-26 — follower is -2 contract(s) off…` | L'écart mesuré, signé, par compte et instrument |
| `Drift cleared on X — entry copies resume` | La reprise |

S'y ajoute le bloc `copier` publié toutes les 500 ms — `resolved`, `drifted`, `drift`,
`lastError` par compte — et l'événement `copierViolation` qui remonte jusqu'à l'hôte.

### Comparer un compte lié à son maître

Les décisions ne suffisent pas à savoir si un compte lié a **obtenu la même chose** que le maître.
Pour ça, trois mécanismes.

**Les exécutions des comptes liés sont enregistrées.** `ExecutionRecorder` n'était abonné qu'au
compte suivi ; le moteur de copie l'abonne désormais au maître **et à chaque compte lié**. Prix,
quantité, commission et P&L des comptes copiés entrent donc dans le journal scellé, et remontent à
Bitlearn comme le reste.

> Le recouvrement avec `OrderMonitor` est voulu — le compte suivi est en général le maître — et
> c'est **l'enregistreur qui dédoublonne**, par identifiant d'exécution. Sans ce garde-fou, chaque
> exécution du compte suivi serait écrite deux fois et **Bitlearn publierait un P&L double**.
> Dédoublonner là plutôt que coordonner les deux abonnements tient quoi qu'on branche ensuite.

**Chaque copie porte l'ordre dont elle vient.** La corrélation est écrite à l'envoi, hors des
cartes de liens — celles-ci se vident dès qu'un ordre devient terminal, et l'exécution peut arriver
après :

```
Copy submitted — master#1042 → Sim101 order#1043 Buy Market qty=2 on MNQ 09-26
```

**Latence et glissement sont mesurés au fill.** Le prix moyen du maître et l'instant de son
exécution sont retenus quand il se remplit ; à l'arrivée du fill de la copie, deux soustractions :

```
Copy filled — master#1042 @20000,25 → Sim101 @20000,5 : 62,4ms de retard, 1 tick de glissement
```

Les mêmes chiffres sont estampillés sur la ligne de journal de l'exécution copiée :
`copyOf`, `copyMaster`, `copyMasterPrice`, `copyLatencyMs`, `copySlippageTicks`.

> **Le glissement est signé pour que positif veuille toujours dire « défavorable au compte lié »**,
> quel que soit le sens : payer plus cher à l'achat et encaisser moins à la vente sont la même
> infortune, et les laisser se compenser dans une moyenne aurait rendu la mesure inutile.
> Convention vérifiée sur les quatre sens.

Ce qui reste hors de portée : une copie dont l'ordre maître n'a jamais rapporté d'exécution n'a
rien à quoi se comparer. Aucun champ n'est alors écrit — plutôt qu'un zéro, qui se lirait comme une
copie parfaite.

## Hors périmètre, assumé

- **pas de correspondance d'instruments** : le suiveur reçoit l'instrument du maître, à l'identique.
  Un MNQ ne devient pas un NQ, et un mois de contrat différent n'est pas géré ;
- **prix recopiés en absolu**, ce qui découle du point précédent ;
- pas de filtre par instrument ni par horaire ;
- pas de copie entre deux instances de NinjaTrader, ni entre deux machines.

## Vérifier

Il n'existe **aucun test automatisé** dans ce dépôt : cette macro se valide en la faisant tourner.
Bridge isolé (`SDBRIDGE_PluginPort=9318`, `SDBRIDGE_AddonPort=9319`, chemins d'état dédiés), deux
comptes Sim au minimum.

| # | Scénario | Attendu |
|---|---|---|
| 1 | Entrée sur le maître | Copiée, quantité multipliée et plafonnée, **par suiveur** |
| 2 | Stop déplacé sur le maître | Suit chez le suiveur, par `Change`, sans recréer l'ordre |
| 3 | Take profit maître exécuté | Stop suiveur annulé par son propre OCO |
| 4 | Layout ancien portant `×2` ou un plafond | Ignoré, ramené à la quantité du maître, ligne de journal |
| 5 | Appui sur la touche Compte | Tous les comptes défilent, groupe compris |
| 6 | Bascule A → B (groupe A, B, C) | On copie vers A et C ; B n'est plus copié. Aucune écriture du layout |
| 7 | Suiveur déconnecté puis reconnecté | `resolved` passe à faux puis à vrai, copie reprise |
| 8 | Rejet marge sur un suiveur | `copierViolation`, touche rouge, `lastError` renseigné |
| 9 | Position fermée à la main sur un suiveur | Dérive détectée, entrées arrêtées, **aucun ordre émis** |
| 10 | Dérive puis retour à plat des deux côtés | Dérive levée d'elle-même |
| 11 | Guard bloque en cours de position | Entrées arrêtées, **sorties toujours copiées** |
| 12 | Recompilation NinjaScript pendant un trade | Configuration republiée, liens perdus, sorties non mappées réconciliées |

## Déploiement

Comme tout changement du moteur : reconstruire l'installateur
(`docs/publier-une-version.md`). L'installateur globe `*.cs` récursivement — `CopyEngine.cs` part
sans qu'il y ait rien à déclarer. Déposer les sources **NinjaTrader ouvert** : il recompile et
recharge de lui-même.
