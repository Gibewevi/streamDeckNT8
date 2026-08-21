# Portage des indicateurs vers ATAS X — conception

> Périmètre : **les indicateurs seuls**. Le chemin d'ordre — remplacer l'add-on NinjaTrader par un
> composant ATAS qui parle au bridge — fait l'objet d'une spécification séparée. Les deux sont
> indépendants : rien ici ne le suppose, rien là-bas n'en dépend.

## Ce qu'on livre

Deux indicateurs ATAS X, `TdSwingStructure` et `TdStopPlacement`, qui produisent la même détection
et le même choix de stop que leurs homologues NinjaTrader — sans en dupliquer l'algorithme.

Le résultat tient en une phrase : **les deux plateformes lisent le même fichier source.** Ce qui est
propre à chacune, c'est le dessin et les réglages ; ce qui est commun, c'est le calcul.

## Pourquoi ce n'est pas une réécriture

NinjaTrader et ATAS restent tous deux en service, tenus par deux équipes. Le risque du projet n'est
donc pas d'écrire du code ATAS — c'est que les deux copies dérivent.

Le dépôt s'est déjà fait mordre par là. `restingVisual.js`, une réécriture à la main des visuels de
touches, avait divergé sur onze points avant qu'on s'en aperçoive ; la réponse fut `emit-shared.mjs`
et la règle « un fichier source, des destinations générées »
(voir `docs/carte-des-deux-depots.md`). La même règle s'applique ici, en plus simple : les deux
projets vivant dans le même dépôt Git, un chemin relatif suffit là où il fallait un script.

## Décisions

### 1. Deux fichiers partagés, liés par MSBuild

```
src/NinjaTrader.Scripts/Indicators/
  TdSwingEngine.cs        PARTAGÉ   inchangé (hors namespace, cf. §3)
  TdStopSelector.cs       PARTAGÉ   extrait de TdStopPlacement.cs
  TdSwingStructure.cs               NT8, inchangé
  TdStopPlacement.cs                NT8, allégé : appelle TdStopSelector

src/Atas.Indicators.TradeDeck/
  Atas.Indicators.TradeDeck.csproj
  TdSwingStructure.cs               ATAS, écrit de zéro
  TdStopPlacement.cs                ATAS, écrit de zéro
```

```xml
<Compile Include="..\NinjaTrader.Scripts\Indicators\TdSwingEngine.cs"  Link="Shared\TdSwingEngine.cs" />
<Compile Include="..\NinjaTrader.Scripts\Indicators\TdStopSelector.cs" Link="Shared\TdStopSelector.cs" />
```

Aucune copie, aucune génération, aucun script. Le fichier a une seule existence sur le disque et
deux compilateurs le lisent.

Ce partage n'est possible que parce que `TdSwingEngine` est une **classe ordinaire** : il n'importe
que `System.Collections.Generic`, pas une ligne de NinjaScript. Cette décision avait été prise pour
contourner l'ordre de compilation de NinjaScript, un problème sans aucun rapport ; elle rend le
portage possible aujourd'hui.

### 2. Le couloir de stop rejoint le partage

`TrySelectAnchor` — le couloir de distance, le saut du pivot le plus évident, le repli ATR — est le
second algorithme pur du projet, et il est aujourd'hui enfoui dans `TdStopPlacement.cs`. Il est
extrait vers `TdStopSelector.cs`, sur le modèle exact de `TdSwingEngine`.

C'est **la seule modification apportée à un fichier NinjaTrader**, et elle ne sort pas du poste de
développement : l'installateur ne livre que `TdSwingEngine.cs`, parce que seul `TrendEngine` en
dépend côté add-on (`build-installer.ps1:146`). `TdStopPlacement` est un instrument de mesure, il
n'est livré à personne.

### 3. Namespace neutre

Les fichiers partagés passent de `NinjaTrader.NinjaScript.Indicators` à `TradeDeck.Analytics`. Sans
ça, l'assembly ATAS contiendrait un namespace `NinjaTrader.*` — sans conséquence fonctionnelle
(ATAS ne recense que les sous-classes d'`Indicator`, ce sont des classes ordinaires), mais de quoi
faire douter le prochain lecteur de ce qu'il regarde.

Coût réel : une ligne `using` dans `TdSwingStructure.cs`, `TdStopPlacement.cs` et `TrendEngine.cs`.
Le nom des fichiers ne changeant pas, l'installateur n'est pas touché.

### 4. Le moteur reste en `double`

ATAS travaille en `decimal`, la conversion se fait aux deux bornes de l'enveloppe ATAS. Passer le
moteur en `decimal` obligerait à réécrire `TdSwingStructure`, `TdStopPlacement` **et**
`TrendEngine` côté NinjaTrader — c'est-à-dire à toucher la macro Tendance, du code de production
qui refuse des ordres.

L'aller-retour est sûr : sur des prix de futures, `(decimal)(double)x` redonne `x`, la conversion
depuis `double` retenant quinze chiffres significatifs. Les prix produits sont de toute façon
arrondis au tick avant affichage.

### 5. `TdStructureViewer` n'est pas porté

La stratégie NinjaScript n'existait que parce qu'une stratégie doit appeler `AddChartIndicator` pour
afficher quoi que ce soit pendant un backtest. Chez ATAS on pose l'indicateur sur le graphique.
La porter produirait une `ChartStrategy` qui ne passe aucun ordre et n'affiche rien de plus.

## Ce qui rend le partage vrai plutôt que décoratif

Une source partagée qui ne casse pas quand on la casse ne sert à rien.

**Les fichiers partagés parlent le langage de NinjaScript, pas celui d'ATAS.** Ils doivent rester
compilables en C# 9 / `net48`. Qui « modernise » `TdSwingEngine` côté ATAS — namespace à portée de
fichier, `record`, `required` — produit du code que NinjaScript refuse, et l'échec ne se voit pas
chez nous : il se voit **chez le trader**, à l'installation, en emportant toute sa compilation
NinjaScript, indicateurs personnels compris.

Le garde-fou existe déjà : `NinjaTrader.Scripts.csproj` compile ces fichiers en `net48` /
`LangVersion 9.0`. Il devient la condition de recette du portage. **Les deux builds passent, jamais
un seul :**

```bash
dotnet build src/NinjaTrader.Scripts/NinjaTrader.Scripts.csproj -c Release        # le garde-fou C# 9
dotnet build src/Atas.Indicators.TradeDeck/Atas.Indicators.TradeDeck.csproj -c Release
```

## Le portage lui-même

### La barre close — le seul piège sérieux

C'est le seul endroit où une traduction littérale produit du code qui marche et des résultats faux.

`TdSwingEngine` est une machine à états qui mute à chaque appel. Chez NinjaTrader,
`Calculate.OnBarClose` garantissait un appel par barre close. Chez ATAS, `OnCalculate` est rappelé
**à chaque tick** de la barre en formation. Deux corruptions distinctes en découlent : nourrir deux
fois la même barre casse l'alternance, et nourrir une barre encore ouverte ancre un extrême sur un
`High` partiel.

```csharp
protected override void OnCalculate(int bar, decimal value)
{
    if (bar == _lastBar) return;
    _lastBar = bar;
    if (bar == 0) return;

    var closed = bar - 1;   // la barre qui vient de CLORE
    ...
}
```

`CurrentBar - 1` est bien la dernière barre close : l'EMA officielle d'ATAS s'en sert pour ses
alertes (`EMA.cs:160`). La passe historique nourrit alors les barres `0 … CurrentBar-1` et laisse la
barre en cours de côté — exactement le comportement NinjaTrader, sans effort supplémentaire.

### Le seuil sera numériquement le même

Un moteur partagé ne sert à rien si son entrée diffère : un autre ATR donne un autre seuil, donc
d'autres pivots. Les formules ont été comparées :

| | NinjaTrader 8 | ATAS |
|---|---|---|
| ATR | `((min(CurrentBar+1,P)-1)·prev + TR) / min(CurrentBar+1,P)`, amorcé `High-Low` | identique, au caractère près |
| EMA | `v·(2/(1+P)) + (1-2/(1+P))·prev`, amorcé `v` | identique |

Même amorçage, même lissage, même rampe de chauffe. On utilise donc les `ATR` et `EMA` natifs
d'ATAS, enregistrés par `Add(_atr)` — le mécanisme d'imbrication qu'emploient `KeltnerChannel` et
`StarcBands`. Rien à porter de plus.

### Le piège du `NaN` disparaît

`decimal` n'a pas de `NaN`. Or l'affichage NinjaTrader repose dessus : chaque plot est remis à
`double.NaN` en début de barre, faute de quoi un point tracé à 0 écrase l'échelle du graphique.

L'idiome ATAS est `ShowZeroValue = false` sur la `ValueDataSeries` : la valeur 0 n'est ni dessinée
ni mise à l'échelle. Comme `0` est déjà la valeur d'initialisation d'une série `decimal`, la remise
à zéro par barre devient inutile. Sans risque : un future ne cote jamais 0.

### Table de traduction

| NinjaTrader 8 | ATAS |
|---|---|
| `State.SetDefaults` | constructeur |
| `State.DataLoaded` | `OnRecalculate()` — remonte le moteur, `_lastBar = -1` |
| `State.Terminated` | `OnDispose()` |
| `OnBarUpdate()` | `OnCalculate(bar, value)`, cf. « la barre close » |
| `AddPlot(Stroke(Brushes.Red,4), Dot, …)` | `ValueDataSeries { Color = Color.Red.Convert(), VisualType = VisualMode.Dots, Width = 4, ShowZeroValue = false }` |
| `Values[0][barsAgo]` (relatif) | `_swingHigh[pivot.ExtremeBar]` (**absolu**) |
| `MaximumBarsLookBack.Infinite` | — le problème n'existe pas |
| `High[0]`, `Low[0]`, `Close[0]` | `GetCandle(closed).High/.Low/.Close` |
| `TickSize` | `InstrumentInfo.TickSize` |
| `Instrument.MasterInstrument.RoundToTickSize(p)` | `Math.Round(p / tick) * tick` — exact en `decimal` |
| `IsOverlay = true` | `Panel = IndicatorDataProvider.CandlesPanel` |
| `[NinjaScriptProperty]` `[Display(Name=…)]` | `[Parameter]` `[Display(Name=…, GroupName=…, Order=…)]`, libellés français en clair |
| setter nu | setter + `RecalculateValues()` — **obligatoire** |
| `Print(...)` | encart `OnRender`, cf. ci-dessous |

### Les statistiques de calibration

ATAS n'a pas de fenêtre de sortie : son `Indicator` n'implémente pas `ILoggerSource`, il n'existe ni
`Print` ni `LogInfo`. Or `docs/strategie-structure-marche.md` est explicite — « régler `k` à l'œil
sur trois journées est lent ; avoir les chiffres à côté en fait une décision ». Les supprimer
viderait le portage de son usage réel.

Elles passent donc dans `OnRender` + `context.DrawString`, un encart dans un coin du graphique sur
le modèle d'`AccountInfoDisplay.cs`, avec un booléen pour l'éteindre. Mêmes chiffres qu'aujourd'hui :
pivots majeurs et mineurs, amplitude moyenne et médiane des vagues en ticks, durée en barres, et
pour `TdStopPlacement` le pourcentage structurel / repli et le taux de balayage.

C'est meilleur que l'original : ces chiffres n'apparaissaient qu'à la fermeture du graphique, ils
sont désormais lisibles en continu.

## Construire et déployer

`.csproj` unique, `net10.0`, sans WPF, références au dossier d'installation :

```xml
<TargetFramework>net10.0</TargetFramework>
<ATAS_BASE Condition="'$(ATAS_BASE)'==''">C:\Program Files\ATAS X</ATAS_BASE>
```

Assemblies référencées : `ATAS.Indicators`, `ATAS.DataFeedsCore`, `OFT.Rendering`, `OFT.Attributes`,
`OFT.Localization`, `Utils.Common`. Aucun paquet NuGet n'est nécessaire.

Un seul `TargetFramework`, parce que la cible est ATAS X. Ajouter l'ATAS classique plus tard sera un
changement de `.csproj` et non de code — **à condition de n'introduire aucun type WPF**, ce que la
conception respecte déjà : `CrossColor` et `System.Drawing.Color`, aucun `UserControl`, aucun XAML.
Un éditeur WPF personnalisé est la seule chose qu'ATAS X ne sait pas convertir.

Déploiement : le DLL dans `%APPDATA%\ATAS X\Indicators\`, ou le bouton « ajouter un indicateur
personnalisé », qui charge sans redémarrer.

Environnement vérifié sur le poste : ATAS X dans `C:\Program Files\ATAS X` (runtime `net10.0`
d'après son `runtimeconfig.json`), SDK .NET 10.0.302, et `%APPDATA%\ATAS X\` en place.

## Comment on saura que c'est juste

Il n'existe aucun test automatisé dans ce dépôt et le portage n'en introduit pas. La vérification
est déjà écrite sans que ce soit son but : **l'encart de statistiques est le test de recette.**

Même instrument, même unité de temps, même session, mêmes réglages, des deux côtés — et on compare
les nombres. Un écart de comptage pointe le séquencement des barres ; un écart d'amplitude pointe le
seuil. Le moteur ne peut pas être en cause, c'est le même fichier.

Recette :

1. les deux builds passent (`NinjaTrader.Scripts` **et** le projet ATAS) ;
2. `TdSwingStructure` posé sur un graphique 200 ticks côté ATAS affiche des pivots aux mêmes
   endroits que côté NinjaTrader, à l'œil, sur une même session rejouée ;
3. les compteurs des deux encarts concordent ;
4. `TdStopPlacement` donne le même partage structurel / repli et la même largeur médiane de stop ;
5. NinjaTrader compile toujours après l'extraction de `TdStopSelector` et le changement de
   namespace — vérifié dans NinjaTrader, pas seulement par `dotnet build`.

## Hors périmètre, à signaler

`TrendEngine` (add-on NinjaTrader) implémente **son propre ATR de Wilder**
(`TrendEngine.cs:113-131`) : moyenne simple sur la première fenêtre, puis lissage. Ce n'est pas la
formule de l'`ATR()` de NinjaScript qu'utilise `TdSwingStructure`, laquelle lisse dès la première
barre. Les deux se réclament pourtant de la même détection avec les mêmes réglages par défaut, et
leurs seuils diffèrent pendant toute la rampe de chauffe.

Divergence interne à NinjaTrader, sans rapport avec le portage. À transmettre à l'équipe NinjaTrader.
