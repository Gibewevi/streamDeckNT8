# Macro « Tendance »

Première macro du deck qui regarde le **marché**. Les règles existantes refusent des ordres pour des
raisons comptables (perte max, budget de trades, plafond de contrats) ou comportementales (pause
obligatoire, Anti-Tilt) ; aucune ne consultait le prix.

Deux régimes, et le second est **optionnel** :

| | |
|---|---|
| **Non armée** | Indicateur pur. La touche montre le sens, rien n'est refusé, et le bridge journalise ce qu'un filtre *aurait* refusé — de quoi calibrer avant de donner la moindre autorité à la macro |
| **Armée** | Les entrées à contre-sens sont **refusées**. Clôturer et réduire restent toujours possibles |

On arme en **maintenant la touche** 1,5 s ; un nouveau maintien désarme. Encore faut-il que le
blocage ait été autorisé dans les réglages de la touche — décoché, la macro n'est pas armable et le
maintien ne fait rien.

Étude complète : `.claude/plans/tudier-la-faisabilit-d-une-gleaming-zephyr.md`.

## Ce qui est livré

| Fichier | Rôle |
|---|---|
| `src/NinjaTrader.AddOn.StreamDeck/Services/TrendEngine.cs` | La détection : ATR et machine à direction. **Classe simple**, aucune dépendance NinjaScript |
| `src/NinjaTrader.AddOn.StreamDeck/Services/TrendMonitor.cs` | Les données : `BarsRequest` par unité de temps, chien de garde, rechargements |
| `src/StreamDeckBridge/Models/TradingState.cs` | Type `TrendState` |
| `src/StreamDeckBridge/StateManager.cs` | L'armement et **le refus** — `IsTrendBlocked` |
| `src/StreamDeckBridge/MessageRouter.cs` | `configureTrend`, `toggleTrend`, et le journal d'observation |
| `src/deck-host/src/catalog.ts`, `visual-engine.ts`, `visuals.ts` | La touche et ses réglages |
| `src/NinjaTrader.AddOn.StreamDeck/Services/ExecutionRecorder.cs` | Le champ `trend` de chaque fill — voir « La mesure contre-tendance » |

## La macro ne lit pas votre graphique

C'est le point à retenir avant tout le reste. L'add-on charge **ses propres barres** par
`BarsRequest` : il ne consulte ni chart, ni indicateur, ni workspace. Trois conséquences :

- elle fonctionne graphique fermé, workspace changé, indicateur retiré ;
- elle fonctionne quel que soit le type de bougies que le trader affiche ;
- elle ne dépend d'aucun réglage de son interface NinjaTrader.

Corollaire : **aucune donnée de marché ne traverse le pont.** Le protocole transporte un verdict
(`up` / `down` / `neutral`), jamais une barre. Les cadences existantes (500 ms de publication,
200 ms de diffusion) sont inchangées.

Le calcul ne pouvait vivre nulle part ailleurs : ni l'hôte (Node) ni le bridge (.NET 8) ne tournent
dans NinjaTrader, donc aucun des deux ne peut demander une barre.

## Comment le sens est déterminé

Une seule méthode, la **structure de marché**. Il n'y a rien à choisir.

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

**Un plancher de 2 ticks est appliqué sous le seuil.** Sur une série atone l'ATR s'effondre vers
zéro et chaque oscillation d'un tick validerait un pivot : la direction deviendrait du bruit
exactement aux moments où elle compte le moins. Même garde que `TdSwingStructure`.

> **Piège si vous relisez `TdSwingEngine`.** `Update` prend deux seuils et empile les pivots des
> deux traqueurs dans **une seule liste**. Avec le même seuil des deux côtés, chaque vague serait
> classée deux fois et « le dernier sommet » désignerait la copie arrivée en tête. D'où le
> `double.MaxValue` passé au traqueur structurel : il ne confirme jamais, et la liste contient
> exactement une entrée par vague.

### Pourquoi pas Heikin Ashi

Un second mode « couleur de la dernière bougie Heikin Ashi » a existé, et il a été **retiré**. Il
répondait moins bien à la même question, pour un coût réel :

- la couleur d'une bougie HA est une statistique à **une seule barre** : elle bascule à chaque
  pullback, c'est-à-dire précisément là où la structure, elle, ne bouge pas ;
- il n'existe qu'une formule Heikin Ashi, donc **rien à régler** — impossible de calibrer sur son
  instrument comme le permet le seuil en ATR ;
- il coûtait un second chemin de code, un seuil de doji qui lui était propre, un réglage sur la
  touche et un champ dans le protocole.

Une méthode qui marche vaut mieux que deux qu'il faut expliquer. Si un autre mode devait revenir un
jour, ce serait plutôt `EMA + bande ATR`, et il se brancherait sans toucher au protocole : le moteur
est une classe ordinaire.

## Barres clôturées uniquement

La barre en cours (`Bars.Count - 1`) n'est **jamais** donnée au moteur. Sa clôture est le prix
courant : une porte qui la lirait autoriserait une entrée puis refuserait la même quelques secondes
plus tard, sans que rien à l'écran n'explique la différence.

Le prix de ce choix est un retard d'**au plus une barre** au retournement. C'est le bon échange.

## L'armement

**Trois portes avant qu'un ordre soit refusé**, et chacune tient à une raison :

1. le blocage doit être **autorisé** dans les réglages de la touche. Faux par défaut, et ce n'est
   pas négociable : une macro capable de refuser des ordres ne s'active pas parce qu'une mise à
   jour est passée. Même règle que la clôture automatique à la perte max ;
2. la macro doit être **armée**, par un maintien de 1,5 s sur la touche ;
3. la tendance doit être **connue**, et l'ordre doit **créer de l'exposition à contre-sens**.

Le maintien vaut 1,5 s : bien au-delà des 600 ms d'une confirmation, qui ne protègent que d'un
frôlement, et très en deçà des 20 s de l'Anti-Tilt, qui sont une punition volontaire. Armer une
protection est un geste délibéré, pas une épreuve. Un appui trop court s'annule sans rien envoyer —
la touche reste un indicateur qu'on peut regarder sans risquer de l'armer par mégarde.

**Décocher l'autorisation désarme.** Masquer une règle sans la neutraliser est le pire des deux
mondes : le trader qui décoche « Autoriser le blocage » doit obtenir un deck qui ne refuse plus
rien, pas une macro restée armée dont plus rien à l'écran ne dit qu'elle l'est. Re-cocher ne réarme
pas — l'armement reste un geste.

**L'armement n'est pas persisté**, et c'est un choix, pas un oubli. Il vit dans `StateManager`, à
côté de la temporisation, dont il reprend exactement la forme : un interrupteur, pas de verrou, un
refus qui ne vise que les entrées. Le bridge ne redémarre qu'à une mise à jour ou à un plantage, la
touche affiche son état en permanence, et une aide à la discipline n'a pas à survivre à un
redémarrage comme le fait le verrou de Guard — lui protège d'un contournement délibéré.

## Trois règles non négociables

1. **Ne jamais refuser une sortie.** Trend ne refuse pas « les achats » mais **les ordres qui créent
   de l'exposition à contre-sens**. En tendance haussière avec un short ouvert, l'achat de clôture
   passe ; c'est la vente qui agrandirait le short qui est refusée. `reverse` s'évalue sur le sens
   qu'il **ouvre**. Même doctrine que `SafetyMacro.CreatesExposure` et
   `GuardEnforcer.GrowsExposure`. Une sonde de bout en bout couvre ce cas précisément.
2. **Ne jamais toucher une position ouverte.** Pas de liquidation, pas de sortie forcée si la
   tendance se retourne pendant le trade.
3. **Pas de verrou.** Ce n'est pas une limite de risque adossée à un fait comptable mais une aide à
   la discipline.

**Ordre d'évaluation** : la Tendance passe **après** la macro de sécurité et **après** la
temporisation, dans le bridge comme dans le rendu de la touche. Ce n'est pas arbitraire — les motifs
de Guard priment, et lire `TREND` sur une touche alors qu'on a atteint sa perte max enverrait
chercher le mauvais problème.

**Une tendance NEUTRE ne refuse rien.** Les deux unités se contredisent : l'indicateur n'a pas
d'avis, donc il n'a aucun titre à refuser. Refuser sur une absence d'avis est la façon la plus sûre
de faire désarmer la macro dès le premier jour.

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
| Changement d'unité de temps | Réglage **structurel** : rechargement |
| Changement de compte | Sans effet — la tendance n'en dépend pas |
| NinjaTrader déconnecté | La tendance est effacée : plus rien ne la met à jour |
| Série rechargée sous nos pieds | Moteur redémarré : l'ATR et la machine à vagues dépendent de l'ordre |
| Série atone | Le plancher de 2 ticks empêche le seuil de s'effondrer avec l'ATR |

## Réglages

| Champ | Défaut | Effet |
|---|---|---|
| `blocageAutorise` | `false` | Rend la macro **armable**. Décoché, elle reste un indicateur |
| `referenceMinutes` | 1 | Unité principale, celle qui donne le sens |
| `higherEnabled` | `true` | Exiger l'accord d'une unité supérieure |
| `higherMinutes` | 5 | Doit être **strictement** supérieure à la référence |
| `thresholdAtr` | 1.0 | Amplitude minimale d'une vague |

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
| Armée | Pastille **`ARM`** blanche en haut à droite, par-dessus l'état ci-dessus |

Hausse et baisse se distinguent par l'**inversion**, exactement comme Achat et Vente : c'est un geste
de lecture déjà appris. **Aucun rouge sur la touche Tendance**, même armée : la charte le réserve au
refus, et une tendance baissière n'est pas un refus — ce sont justement les ventes qui sont
autorisées. La pastille est blanche pour rester lisible sur les deux fonds.

Le rouge apparaît là où il veut dire quelque chose : **sur les touches d'entrée refusées**, qui
affichent `TREND` à côté de `LOSS LIMIT` et `PAUSE`. Et seulement sur celles qui créeraient
l'exposition refusée — celle qui permet de sortir reste intacte, sinon on se croirait enfermé.

Ligne du bas : les deux unités séparément (`1m UP · 5m DN`). Sans elle, un `FLAT` ne dirait pas si
le marché hésite ou si les deux unités se contredisent.

> Le symbole est un **`<polygon>` SVG**, pas une flèche typographique — comme les touches
> Stop/Target. Le boîtier épingle **une seule police** (`arialbd.ttf`, `loadSystemFonts: false`) :
> un glyphe géométrique absent se dessinerait en tofu, et aucune compilation ne le signalerait.

Un **maintien** de 1,5 s arme ou désarme, jauge à l'appui. Un appui bref ne fait que redemander
l'état — et quand le blocage n'est pas autorisé, le maintien lui-même ne fait rien.

## Le journal — à lire AVANT d'armer

Deux chiffres sortent d'une séance passée macro désarmée, et ce sont eux qui disent si elle est
armable sans devenir insupportable.

**Combien de fois le sens bascule.** Journal de l'add-on, catégorie `Trend`, au **changement**
seulement :

```
[Trend] Trend is UP — 1min=up, 5min=up
[Trend] Trend is DOWN — 1min=down, 5min=down
[Trend] Trend UNAVAILABLE — bars missing, still loading, or stale
```

**Combien d'appuis auraient été refusés.** Journal du bridge, en `INFO` — ce n'est pas une boucle
périodique mais un appui de touche. Écrit uniquement **macro désarmée** : armée, c'est un vrai refus
qui est journalisé, pas une observation.

```
[REQ:…] TREND observation — buyMarket WOULD HAVE BEEN REFUSED: trend is down
        (1min=down, 5min=down) on MNQ 09-26
[REQ:…] TREND blocked buyMarket: Trend is DOWN (1min=down, 5min=down): buying against it
        is refused while the Trend macro is armed. Closing and reducing stay available.
```

Méthode de calibration : laisser tourner une demi-séance **sans armer**, compter. Un sens qui
bascule toutes les deux minutes veut dire que `thresholdAtr` est trop bas. Un nombre de refus proche
du nombre total d'entrées veut dire que la règle, une fois armée, sera désarmée dans la semaine.

Fichiers : `%APPDATA%\StreamDeckTrader\` (`bridge-` et `addon-AAAA-MM-JJ.log`), corrélés par
`requestId`.

## La mesure contre-tendance — native, sans rien armer

Le journal enregistre le sens de la tendance **sur chaque exécution**, et Bitlearn en tire un
pourcentage de trades pris à contre-sens ainsi qu'une étiquette dans l'historique.

**Aucun réglage ne la commande.** Ni l'armement, ni même la présence de la touche Tendance sur le
boîtier : `TrendMonitor` tourne depuis le démarrage de l'add-on, `StatePublisher` lui passe
l'instrument suivi à chaque tick, et l'armement ne gouverne que le refus d'un ordre — jamais le
calcul. Sans touche Tendance dans la disposition, `configureTrend` n'est jamais envoyé et le
moniteur travaille sur ses valeurs par défaut (1 min confirmée par 5 min, seuil 1,0 ATR).

C'est **l'exécution** qui porte le verdict, et non un événement comportemental. La raison tient au
profil visé : plusieurs trades par minute. Les événements `position.opened` naissent d'une
comparaison entre deux états diffusés à 5 Hz, donc un aller-retour de deux secondes peut n'en
produire aucun ; un fill n'est jamais manqué.

```json
{"kind":"exec","execId":"…","marketPosition":"Long","trend":"down", …}
```

Trois valeurs, et **le champ peut être absent** :

| Valeur | Sens | Compté ? |
|---|---|---|
| `up` / `down` | Le marché a un sens | Oui — contre-tendance si l'entrée l'affronte |
| `neutral` | Le marché ne va nulle part | Oui, au dénominateur : le trade n'allait contre rien |
| *(absent)* | Le poste ne savait pas — série absente, périmée, ou fill sur un autre contrat que celui suivi | Non, ni au numérateur ni au dénominateur |

La dernière ligne est celle qui compte : traiter « on ne sait pas » comme « dans le sens » ferait
d'une panne de flux une séance sans faute. `TrendMonitor.DirectionFor` rend `null` pour ces trois
situations indistinctement, et `ExecutionRecorder` omet alors la clé.

Le fill d'un contrat que le moniteur ne suit pas n'est jamais estampillé : le moniteur suit
l'instrument sélectionné sur le deck, et lui coller sa direction produirait un fait fabriqué que
rien, en aval, ne distinguerait d'une mesure.

Côté Bitlearn : colonne `tradedeck_executions.trend`, `estContreTendance` dans
`lib/tradeDeck/psychology.js`, jauge « Contre-tendance » et étiquette du même nom dans l'historique.
L'entrée d'un aller-retour est sa première exécution dans le temps — un retournement ayant été
découpé en deux allers-retours par le rollup, chacun retrouve la sienne.

## Protocole

Bloc `trend` dans `stateUpdate`, commandes `configureTrend` et `toggleTrend`, codes
`TREND_AGAINST` et `TREND_BLOCKING_DISABLED` : voir [protocol.md](protocol.md).

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

**Depuis 0.17.0, l'installateur dépose ces fichiers lui-même** — les sources de l'add-on dans
`AddOns\StreamDeck\` et `TdSwingEngine.cs` dans `Indicators\`. La copie manuelle ci-dessus ne sert
plus qu'au poste de développement.

Reste à **compiler** : NinjaTrader charge son assemblage déjà compilé et ignore la date des
sources, donc redémarrer ne déclenche rien. *Control Center → New → NinjaScript Editor → F5*.

Le dédoublement reste le danger : l'installateur efface les `.cs` de `AddOns\StreamDeck\` avant
d'écrire les siens, ce qui rattrape une copie égarée de `TdSwingEngine.cs` déposée là à la
main, mais il ne peut rien contre un exemplaire caché ailleurs dans `bin\Custom`.

## Vérifier

```bash
dotnet build "src/NinjaTrader.AddOn.StreamDeck/NinjaTrader.AddOn.StreamDeck.csproj" -c Release   # CS0436 attendus
dotnet build "src/StreamDeckBridge/StreamDeckBridge.csproj" -c Release
cd src/deck-host && npx tsc --noEmit
```

Deux choses se testent réellement ici, contrairement au reste du projet. **Il faut les lancer.**

**Le moteur**, qui ne dépend d'aucune API NinjaTrader : un projet console `net48` du scratchpad
compilant `TdSwingEngine.cs` + `TrendEngine.cs` suffit. C'est ainsi que la tendance, le retournement
sur cassure, la conservation du sens en range, le plancher de ticks et le préchauffage ont été
validés. Même méthode que pour `SdLogger` et `SimpleJson`.

**Le refus**, contre un bridge isolé (`SDBRIDGE_PluginPort=9318`, `SDBRIDGE_AddonPort=9319`, et les
chemins d'état vers le scratchpad — **ne jamais sonder le port 8218**). Un faux add-on publie un
`stateUpdate` porteur d'une tendance et d'une position, le côté plugin envoie des ordres, et l'on
vérifie qui est refusé. C'est le seul moyen d'exercer le chemin de refus sans NinjaTrader, et il
couvre le cas qui compte : **tendance haussière, short ouvert, l'achat de clôture doit passer**
pendant que la vente qui agrandirait le short est refusée.

Scénarios à passer en séance :

1. le sens ne bascule qu'à la cassure d'un extrême, pas à chaque pullback ;
2. changer d'instrument → `NO DATA` quelques secondes, **aucune touche rouge, aucun ordre refusé** ;
3. couper le flux → `NO DATA`, puis rechargement automatique par le chien de garde ;
4. décocher « unité supérieure » → `higherMinutes` **neutralisé**, pas seulement masqué ;
5. changer l'unité de temps dans l'éditeur → séries rechargées sans redémarrer l'hôte ;
6. blocage **non autorisé** : maintenir la touche → rien ne s'arme, aucun ordre n'est refusé ;
7. blocage autorisé, maintien → pastille `ARM`, puis Achat en tendance baissière → **refusé**, la
   touche affiche `TREND` en rouge et le journal porte `TREND blocked` ;
8. **le test qui compte** : armée en tendance haussière avec un short ouvert → Achat de clôture,
   `Flatten` et `Tout fermer` doivent **tous** passer ;
9. décocher « Autoriser le blocage » pendant que la macro est armée → elle **désarme**, la pastille
   disparaît, les ordres repassent.

## Ce qui reste ouvert

**L'accord strict des deux unités produit beaucoup de `FLAT`.** Sur la séance du 11/08, 5 verdicts
sur 9 étaient neutres parce que le 1 min et le 5 min se contredisaient. Neutre ne refuse rien, donc
c'est sans danger — mais une macro armée qui n'a d'avis que la moitié du temps protège aussi moitié
moins. Deux leviers, à trancher sur les journaux : desserrer `thresholdAtr`, ou passer à une
hiérarchie « le 5 min décide, le 1 min informe » plutôt qu'un accord strict.

Une troisième méthode de détection (`EMA + bande ATR`) reste branchable sans toucher au protocole :
le moteur est une classe ordinaire.
