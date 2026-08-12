# Macro « Tendance » — version 1 : afficher et mesurer

Première macro du deck qui regarde le **marché**. Les règles existantes refusent des ordres pour des
raisons comptables (perte max, budget de trades, plafond de contrats) ou comportementales (pause
obligatoire, Anti-Tilt) ; aucune ne consultait le prix.

> **Cette version ne refuse rien.** Elle affiche le sens du marché sur une touche, et le bridge
> journalise ce qu'un filtre *aurait* refusé. Le refus est le lot suivant, et il n'a de sens
> qu'après avoir lu ces chiffres sur une vraie séance.

Étude complète : `.claude/plans/tudier-la-faisabilit-d-une-gleaming-zephyr.md`.

## Ce qui est livré

| Fichier | Rôle |
|---|---|
| `src/NinjaTrader.AddOn.StreamDeck/Services/TrendEngine.cs` | La détection : ATR, Heikin Ashi, machine à direction. **Classe simple**, aucune dépendance NinjaScript |
| `src/NinjaTrader.AddOn.StreamDeck/Services/TrendMonitor.cs` | Les données : `BarsRequest` par unité de temps, chien de garde, rechargements |
| `src/StreamDeckBridge/Models/TradingState.cs` | Type `TrendState`, transporté sans être interprété |
| `src/StreamDeckBridge/MessageRouter.cs` | `configureTrend`, et le **journal d'observation** |
| `src/deck-host/src/catalog.ts`, `visual-engine.ts`, `visuals.ts` | La touche et ses réglages |

## La macro ne lit pas votre graphique

C'est le point à retenir avant tout le reste. L'add-on charge **ses propres barres** par
`BarsRequest` : il ne consulte ni chart, ni indicateur, ni workspace. Trois conséquences :

- elle fonctionne graphique fermé, workspace changé, indicateur retiré ;
- elle fonctionne chez quelqu'un qui n'a **jamais installé Heikin Ashi** ;
- le choix Heikin Ashi devient un réglage de la macro, pas une dépendance à votre configuration.

Corollaire : **aucune donnée de marché ne traverse le pont.** Le protocole transporte un verdict
(`up` / `down` / `neutral`), jamais une barre. Les cadences existantes (500 ms de publication,
200 ms de diffusion) sont inchangées.

Le calcul ne pouvait vivre nulle part ailleurs : ni l'hôte (Node) ni le bridge (.NET 8) ne tournent
dans NinjaTrader, donc aucun des deux ne peut demander une barre.

## Deux méthodes

### `Structure` — le défaut

```
H = dernier sommet confirmé, L = dernier creux confirmé
clôture > H  → HAUSSIER
clôture < L  → BAISSIER
sinon        → on garde le sens précédent
```

Les pivots viennent de [`TdSwingEngine`](../src/NinjaTrader.Scripts/Indicators/TdSwingEngine.cs),
déjà écrit pour la détection de structure de marché : machine à états alternée, seuil exprimé en
**multiples d'ATR** et non en ticks ni en pourcentage. C'est cette propriété qui fait qu'un même
réglage tient sur MNQ en 1 min et sur ES en 15 min — l'ATR absorbe le changement de régime.
Raisonnement complet dans [strategie-structure-marche.md](strategie-structure-marche.md).

Deux choix méritent d'être explicites :

**On bascule sur la CASSURE, jamais sur la confirmation du pivot.** La limite énoncée dans l'étude
de structure — « un pivot n'est confirmable qu'après coup » — ne mord donc pas ici : `H` et `L` sont
connus *avant* d'être franchis, on ne fait que les comparer au prix. Le sens n'est pas en retard.

**L'hystérésis est gratuite.** Repartir dans l'autre sens exige de casser l'extrême opposé : une
oscillation à l'intérieur du couloir laisse la direction tranquille. C'est tout le mécanisme
anti-whipsaw, et il ne coûte aucun paramètre supplémentaire.

Seuil par défaut : **`1.0 × ATR(20)`**, et non `2.5`. Le niveau structurel à 2,5 sert à repérer les
poches de liquidité ; le niveau intermédiaire à 1,0 existe justement pour attraper les pullbacks de
tendance. C'est celui-là que la direction veut.

> **Piège si vous relisez `TdSwingEngine`.** `Update` prend deux seuils et empile les pivots des
> deux traqueurs dans **une seule liste**. Avec le même seuil des deux côtés, chaque vague serait
> classée deux fois et « le dernier sommet » désignerait la copie arrivée en tête. D'où le
> `double.MaxValue` passé au traqueur structurel : il ne confirme jamais, et la liste contient
> exactement une entrée par vague.

### `Heikin Ashi`

Le corps de la dernière bougie **clôturée** : `HAClose > HAOpen` = haussier.

Formule identique à celle de `HeikenAshi8.cs`, l'indicateur officiel NinjaTrader qui vit dans
`Documents\NinjaTrader 8\bin\Custom\Indicators\` :

```
HAClose[0] = (Open[0] + High[0] + Low[0] + Close[0]) / 4
HAOpen[0]  = (HAOpen[1] + HAClose[1]) / 2
```

Recalculée plutôt que demandée en `BarsPeriodType.HeikenAshi` : le type de barre natif applique la
même formule mais **arrondit au tick**, et surtout constituerait une *série de plus* à charger. Une
seule série Minute brute nourrit la structure, HA, et une future EMA.

**L'amorçage n'est pas un problème.** `HAOpen` dépend de tout l'historique depuis la première barre
chargée, qui n'est pas celle où commence votre graphique. Mais la récurrence **divise l'écart par
deux à chaque barre** : après 20 barres l'erreur est un millionième de l'écart initial, très en
dessous du tick. Vérifié par un test (« direction survives a different seed »).

**Limite connue de la méthode** : la couleur HA est une statistique à *une* bougie. Elle bascule à
chaque pullback, et il n'existe qu'une formule HA — donc rien à régler. C'est pour cela que
`Structure` est le défaut.

> **Attention si vous comparez à votre écran.** Votre `HeikenAshi8.cs` porte une personnalisation
> « zones RSI » : elle repeint le corps en **rouge** au-dessus de RSI 70 et en **vert** en dessous
> de 30. Une bougie *haussière* apparaît donc **rouge** en surachat, et la touche vous contredira
> dans les extrêmes. Ce n'est pas un défaut de la macro.

> **Et si la structure était calculée sur des barres HA ?** Elle ne l'est pas, volontairement. Les
> mèches Heikin Ashi sont synthétiques (bornées par le corps) : un pivot détecté dessus n'est pas un
> niveau de prix réel. Sans conséquence pour une direction, rédhibitoire pour un stop — et
> `TdSwingEngine` sert déjà à en poser un dans `TdStopPlacement`.

## Barres clôturées uniquement

La barre en cours (`Bars.Count - 1`) n'est **jamais** donnée au moteur. Sa clôture est le prix
courant : son corps Heikin Ashi bascule plusieurs fois par minute. Une porte qui la lirait
autoriserait une entrée puis refuserait la même cinq secondes plus tard, sans que rien à l'écran
n'explique la différence.

Le prix de ce choix est un retard d'**au plus une barre** au retournement. C'est le bon échange.

## Trois règles non négociables

Elles ne s'appliquent pas encore — rien n'est refusé — mais elles sont déjà respectées par le
journal d'observation, pour que les chiffres qu'il produit soient ceux de la règle réelle.

1. **Ne jamais refuser une sortie.** Trend ne refusera pas « les achats » mais **les ordres qui
   créent de l'exposition à contre-sens**. En tendance haussière avec un short ouvert, l'achat de
   clôture doit passer. `reverse` s'évalue sur le sens qu'il **ouvre**. Même doctrine que
   `SafetyMacro.CreatesExposure` et `GuardEnforcer.GrowsExposure`.
2. **Ne jamais toucher une position ouverte.** Pas de liquidation, pas de sortie forcée si la
   tendance se retourne pendant le trade.
3. **Pas de verrou.** Ce n'est pas une limite de risque adossée à un fait comptable mais une aide à
   la discipline.

**Tendance inconnue → on laisse passer, et on le dit.** Même posture que `pnlAvailable: false` sur
les règles de perte : signaler, jamais bloquer ni autoriser en silence. Un roll de contrat ou un
flux figé ne doivent pas couper les entrées sans explication. La touche affiche `NO DATA` en éteint,
jamais en rouge.

**`GuardEnforcer` n'est pas étendu à Trend.** Les ordres passés directement dans NinjaTrader
continuent d'échapper à cette macro, et c'est voulu : annuler un ordre posé à la main sur le
graphique parce qu'un creux n'a pas été cassé serait incompréhensible. Guard est une limite de
compte ; Trend est un filtre discrétionnaire sur les touches du deck.

## Fiabilité des données

**Le chien de garde est la pièce la plus importante du monitor.** Sans lui, un flux figé laisserait
la dernière direction connue debout pendant des heures — et, une fois le refus livré, la macro
continuerait de refuser dessus.

Deux détails de conception qui répondent chacun à une façon de le rater :

- **la fraîcheur est évaluée par le LECTEUR**, pas par le gestionnaire d'événements. Un flux mort ne
  lève aucun `Update` : un contrôle logé dans le handler serait le seul contrôle à ne jamais tourner
  précisément quand il sert. Le verdict transporte donc le moment où chaque série a avancé, et
  `BuildState` — appelé par un timer qui, lui, continue de battre — décide s'il vaut encore quelque
  chose ;
- **on mesure le temps écoulé depuis l'ARRIVÉE de la barre**, jamais l'écart avec son horodatage.
  NinjaTrader horodate les barres dans le fuseau d'affichage configuré par l'utilisateur, qui n'est
  pas nécessairement celui de la machine : soustraire l'un de l'autre lirait des heures de retard
  sur un flux parfaitement sain et éteindrait la tendance pour de bon.

Seuil : deux périodes sans barre clôturée. Hors séance, cela répond « périmé », et c'est correct —
pas de données, pas de verdict, rien de refusé.

| Situation | Comportement |
|---|---|
| Changement d'instrument | Séries détruites et rechargées ; `NO DATA` le temps du chargement |
| Roll de contrat | Rechargement sur changement du **nom complet résolu**, pas de la racine |
| Changement d'unité de temps ou de méthode | Réglage **structurel** : rechargement |
| Changement de compte | Sans effet — la tendance n'en dépend pas |
| NinjaTrader déconnecté | La tendance est effacée : plus rien ne la met à jour |
| Série rechargée sous nos pieds | Moteur redémarré : les deux récurrences sont dépendantes de l'ordre |
| Gap overnight | 2-3 barres HA déformées, se corrige seul |
| Doji HA | Corps sous `0.1 × ATR` (plancher 2 ticks) : le sens **ne bascule pas**. Un doji marque une pause dans la tendance, pas un retournement |

## Réglages

| Champ | Défaut | Effet |
|---|---|---|
| `trendMethod` | `structure` | Structure de marché ou Heikin Ashi |
| `referenceMinutes` | 1 | Unité principale, celle qui donne le sens |
| `higherEnabled` | `true` | Exiger l'accord d'une unité supérieure |
| `higherMinutes` | 5 | Doit être **strictement** supérieure à la référence |
| `thresholdAtr` | 1.0 | Amplitude minimale d'une vague, méthode `structure` seulement |

Accord **strict** quand l'unité supérieure est active : en désaccord, le verdict est `neutral` et la
touche affiche `FLAT`. Neutre est la réponse honnête — les deux unités racontent deux histoires
différentes, et en choisir une masquerait ce fait.

Les bornes sont doublées entre le bridge et l'add-on, délibérément : refuser dans le bridge produit
un message que l'éditeur affiche, alors qu'un rabotage silencieux dans l'add-on laisserait l'écran
annoncer un réglage que la macro n'applique pas.

## La touche

| État | Rendu |
|---|---|
| Haussier | Orange plein, triangle blanc vers le haut |
| Baissier | Noir, triangle orange vers le bas |
| Divergent / neutre | Éteint, barre horizontale |
| Sans données | Éteint, `NO DATA` |

Hausse et baisse se distinguent par l'**inversion**, exactement comme Achat et Vente : c'est un geste
de lecture déjà appris. **Aucun rouge** : la charte le réserve au refus, et une tendance baissière
n'est pas un refus — ce sont justement les ventes qui seront autorisées.

Ligne du bas : les deux unités séparément (`1m UP · 5m DN`). Sans elle, un `FLAT` ne dirait pas si
le marché hésite ou si les deux unités se contredisent.

> Le symbole est un **`<polygon>` SVG**, pas une flèche typographique — comme les touches
> Stop/Target. Le SVG est rendu par resvg dans les polices du système : un glyphe géométrique absent
> se dessinerait en tofu, et aucune compilation ne le signalerait.

L'appui ne fait que redemander l'état. **La touche ne s'arme pas**, et c'est délibéré : la macro ne
refuse rien, donc un interrupteur ferait croire à une protection qui n'existe pas.

## Le journal, qui est le vrai livrable

Deux chiffres sortent d'une séance, et ce sont eux qui décideront de la suite.

**Combien de fois le sens bascule.** Journal de l'add-on, catégorie `Trend`, au **changement**
seulement :

```
[Trend] Trend is UP — 1min=up, 5min=up
[Trend] Trend is DOWN — 1min=down, 5min=down
[Trend] Trend UNAVAILABLE — bars missing, still loading, or stale
```

**Combien d'appuis auraient été refusés.** Journal du bridge, en `INFO` — ce n'est pas une boucle
périodique mais un appui de touche :

```
[REQ:…] TREND observation — buyMarket WOULD HAVE BEEN REFUSED: trend is down
        (1min=down, 5min=down) on MNQ 09-26, method=structure
```

Méthode de calibration : laisser tourner une demi-séance, compter. Un sens qui bascule toutes les
deux minutes veut dire que `thresholdAtr` est trop bas. Un nombre de refus proche du nombre total
d'entrées veut dire que la règle, une fois armée, sera désarmée dans la semaine.

Fichiers : `%APPDATA%\StreamDeckTrader\` (`bridge-` et `addon-AAAA-MM-JJ.log`), corrélés par
`requestId`.

## Protocole

Bloc `trend` ajouté à `stateUpdate`, et commande `configureTrend` : voir
[protocol.md](protocol.md).

## Déploiement — le piège à ne pas rater

`TrendEngine` référence `TdSwingEngine`, qui vit dans `Indicators\`. L'add-on et les indicateurs
compilent dans le **même** `NinjaTrader.Custom.dll`, donc la référence traverse les dossiers sans
difficulté. Mais :

> **`TdSwingEngine.cs` doit exister en EXACTEMENT UNE copie sur le disque.** Un second exemplaire
> sous `AddOns\StreamDeck\` produit un `CS0101` (type dupliqué), et une compilation NinjaScript est
> tout-ou-rien : cet échec emporterait aussi les indicateurs et stratégies personnels du trader.

```powershell
$nt = "$env:USERPROFILE\Documents\NinjaTrader 8\bin\Custom"
Copy-Item "src\NinjaTrader.Scripts\Indicators\TdSwingEngine.cs" "$nt\Indicators\" -Force
```

Le `<Compile Include… Link=…>` du `.csproj` de l'add-on ne sert qu'à la **vérification de
compilation locale** et ne se déploie jamais.

## Vérifier

```bash
dotnet build "src/NinjaTrader.AddOn.StreamDeck/NinjaTrader.AddOn.StreamDeck.csproj" -c Release   # CS0436 attendus
dotnet build "src/StreamDeckBridge/StreamDeckBridge.csproj" -c Release
cd src/deck-host && npx tsc --noEmit
```

**`TrendEngine` se teste réellement**, contrairement au reste du projet : il ne dépend d'aucune API
NinjaTrader. Un projet console `net48` du scratchpad compilant `TdSwingEngine.cs` + `TrendEngine.cs`
suffit — c'est ainsi que la tendance en structure, la formule HA, la convergence d'amorçage, le doji
et le préchauffage ont été validés. Même méthode que pour `SdLogger` et `SimpleJson`.

Scénarios à passer en séance :

1. méthode `Heikin Ashi`, graphique 1 min ouvert : le sens suit les bougies clôturées ;
2. méthode `Structure` : le sens ne bascule qu'à la cassure d'un extrême, pas à chaque pullback ;
3. changer d'instrument → `NO DATA` quelques secondes, **aucune touche rouge, aucun ordre refusé** ;
4. couper le flux → `NO DATA` au bout de deux périodes ;
5. décocher « unité supérieure » → `higherMinutes` **neutralisé**, pas seulement masqué ;
6. changer l'unité de temps dans l'éditeur → séries rechargées sans redémarrer l'hôte ;
7. **le test de périmètre** : touche affichant `DOWN`, appuyer sur Achat → **l'ordre part**, et le
   journal du bridge porte la ligne « WOULD HAVE BEEN REFUSED ».

## Le lot suivant

Écrit une fois ces journaux lus, et pas avant : `TrendGate.cs` dans le bridge (classe distincte de
`SafetyMacro` — pas de verrou, pas de persistance de séance), code d'erreur `TREND_AGAINST`,
armement par la touche, libellé `TREND` en rouge dans `entryStatus` à côté de `LOSS LIMIT` et
`PAUSE`, sur les seules touches qui créeraient l'exposition refusée.

Ordre d'évaluation prévu, et il n'est pas arbitraire : **après** la macro de sécurité et **après**
le cooldown. Les messages de Guard doivent gagner — lire `TREND` alors qu'on a atteint sa perte max
enverrait chercher le mauvais problème. Le journal d'observation est déjà placé à cet endroit exact.

Une troisième méthode (`EMA + bande ATR`) se branche sans toucher au protocole : le moteur est une
classe, la méthode est une chaîne dans le payload.
