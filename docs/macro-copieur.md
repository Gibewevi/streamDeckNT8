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
  Compte maître ....... Sim101        (= le compte sélectionné, cyclé par la touche)
  Copier les positions  ON
  Suiveurs
    → Sim102   × 1    plafond 0
    → Sim103   × 2    plafond 5
    → APEX-4471 × 1   plafond 3
```

- recopie des entrées, des sorties, des déplacements de stop/target et des annulations ;
- **multiplicateur et plafond de contrats par suiveur**, arrondi entier ;
- **OCO reconstruit côté suiveur**, si bien que le courtier tient le bracket même si le copieur
  décroche ;
- **contrôle de dérive qui arrête un suiveur, sans jamais tenter de le corriger.**

## Le maître est le compte sélectionné

Il n'existe pas de réglage « compte maître » : c'est le compte que la touche Compte a sélectionné,
celui sur lequel le deck envoie déjà ses ordres. Deux conséquences, toutes deux voulues.

**Les comptes suiveurs sortent du défilement de la touche.** Sans cela, un appui suffirait à
sélectionner un suiveur, qui deviendrait maître de lui-même — et se copierait vers ses propres
pairs. Tant que la copie est active, les suiveurs sont retirés de la liste de défilement, comme
REPEATER9000 retirait déjà les meneurs de la liste des suiveurs assignables.

**Changer de compte maître désactive la copie.** Les positions ouvertes chez les suiveurs
appartiennent au maître précédent ; continuer à copier depuis un autre compte mélangerait deux
sources dans une même position, ce qui est précisément la divergence que la section Dérive existe
pour empêcher. La copie se coupe, la touche le montre, le journal le dit, et le trader la
réactive délibérément. Fermeture par sécurité, jamais par surprise.

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
| Quantité suiveur = quantité maître, sans réglage | Multiplicateur et plafond **par suiveur** (§ [Dimensionnement](#dimensionnement)) |
| Comptes découverts **une seule fois**, à l'ouverture de la fenêtre : un compte reconnecté n'était plus jamais copié, et l'écran le montrait quand même configuré | Résolution refaite à chaque publication d'état, et `resolved: false` remonté par suiveur — un suiveur non résolu se voit |
| Indicateur d'activation `bool` non `volatile`, lu depuis les threads d'événements | Un objet de configuration immuable, échangé atomiquement |
| Rejet d'un ordre suiveur traité comme une annulation : lien supprimé, rien remonté | Événement `copierViolation` → bridge → hôte, touche rouge, ligne de journal |
| Aucune surveillance de l'écart maître / suiveur | § [Dérive](#la-dérive--on-sarrête-on-ne-répare-pas) |
| `Trace.WriteLine` | `SdLogger`, catégorie `Copier`, corrélé comme le reste |
| Threads de travail jamais arrêtés | Arrêtés dans le `Shutdown()` de l'add-on — NinjaScript recharge l'add-on à chaque recompilation |
| Sondes de latence dans un CSV à part | Mesures dans le journal existant |

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

Pour chaque suiveur, avec **son** multiplicateur et **son** plafond :

```
quantité = arrondi( quantité_maître × multiplicateur )
quantité = min( quantité, plafondContrats )      si plafondContrats > 0
```

- **arrondi entier**, à l'entier le plus proche. Un contrat est indivisible ;
- un résultat de **0 n'envoie rien** et se journalise : c'est le cas d'un multiplicateur de 0,3 sur
  un ordre d'un contrat. Ne pas arrondir à 1 en douce — un suiveur réglé pour prendre un tiers du
  risque ne doit pas se retrouver à en prendre la totalité ;
- **`multiplicateur = 0` désactive le suiveur** sans le retirer de la liste ;
- le plafond est un plafond **par ordre**, pas une limite de position. La limite de position reste
  celle de Guard (`maxContracts`), qui s'applique compte par compte.

## La dérive — on s'arrête, on ne répare pas

**C'est la règle qui prime sur tout le reste de ce document.**

Un copieur d'ordres n'est pas un copieur de positions. Un rejet pour marge insuffisante, un fill
partiel d'un seul côté, un redémarrage en pleine position : le maître et le suiveur divergent.
REPEATER9000 ne le remarque jamais.

Ici, à chaque publication d'état, on compare pour chaque couple (suiveur, instrument) :

```
position attendue = position nette du maître × multiplicateur   (arrondie)
position réelle   = position nette du suiveur
```

Deux natures d'écart, jugées différemment :

| Écart | Jugé ? |
|---|---|
| **Sens opposé**, maître à plat et suiveur non plat, ou l'inverse | **Toujours.** C'est le cas dangereux : une exposition que personne n'a voulue |
| **Taille seule**, même sens | Seulement si **aucun plafond n'est actif** sur ce suiveur — un plafond crée un écart légitime et attendu, le signaler serait une fausse alerte permanente |

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
| Dérive ou rejet sur un suiveur | `ACC-101` / `COPY STOP`, détail `DERIVE` ou `REJET`, rouge |
| Copie retenue (maître changé) | `ACC-101` / `COPY HOLD`, détail `MAITRE`, rouge |

`COPY HOLD` est en tête de liste et en rouge délibérément : c'est le seul état où le trader **croit
que la copie tourne alors qu'elle est arrêtée**, et cet écart-là vaut plus qu'un avertissement.

Appui : défilement du compte, suiveurs exclus. La copie ne se coupe pas depuis le boîtier — c'est
un réglage, pas une commande de séance ; son interrupteur est dans les réglages de la touche.

Au repos — copie éteinte, ou aucun suiveur — la touche rend **exactement** comme avant : c'est ce
que vérifient les 28 instantanés de `deckPreview.test.js` côté Bitlearn, tous verts sans mise à jour.

## Réglages de la touche Compte

| Clé | Type | Rôle |
|---|---|---|
| `accounts` | `textarea` | *(existant)* Ordre et filtre du défilement |
| `journal` | `toggle` | *(existant)* Journaliser ce compte vers Bitlearn |
| `copyEnabled` | `toggle` | Copier les positions vers les comptes suiveurs |
| `followers` | `followerList` | Les suiveurs, leur multiplicateur et leur plafond |

> **`followers` est une CHAÎNE, pas un tableau — et ce n'est pas négociable.**
> `sanitizeSettings` (`Bitlearn/lib/tradeDeck/layout.js`) n'accepte que `string`, `boolean` et
> `number` dans les réglages d'une touche : un tableau ou un objet est **silencieusement écarté**
> en traversant le site, et le trader verrait sa sélection disparaître sans un message.

Format : **une ligne par suiveur**, `nom|multiplicateur|plafond`.

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
| 4 | Multiplicateur 0,3 sur 1 contrat | Rien envoyé, ligne de journal |
| 5 | Appui sur la touche Compte | Les suiveurs sont **absents** du défilement |
| 6 | Changement de compte maître | Copie désactivée, raison journalisée |
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
