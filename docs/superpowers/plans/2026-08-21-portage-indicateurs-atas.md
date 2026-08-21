# Portage des indicateurs vers ATAS X — plan d'implémentation

> **Pour un agent :** SOUS-COMPÉTENCE REQUISE — utiliser `superpowers:subagent-driven-development`
> (recommandé) ou `superpowers:executing-plans` pour exécuter ce plan tâche par tâche. Les étapes
> utilisent la syntaxe à cases (`- [ ]`).

**Objectif :** livrer `TdSwingStructure` et `TdStopPlacement` sur ATAS X, sans dupliquer une seule
ligne d'algorithme avec la version NinjaTrader.

**Architecture :** les deux algorithmes purs (`TdSwingEngine`, `TdStopSelector`) restent des classes
ordinaires dans `src/NinjaTrader.Scripts/Indicators/`, et le projet ATAS les compile par lien
MSBuild — un seul fichier sur le disque, deux compilateurs. Seuls l'affichage et les réglages sont
écrits deux fois, parce qu'ils sont propres à chaque plateforme.

**Pile technique :** C# 9 / `net48` pour les fichiers partagés (contrainte NinjaScript), C# récent /
`net10.0` pour le projet ATAS, DLL référencées depuis `C:\Program Files\ATAS X`.

**Spécification :** `docs/superpowers/specs/2026-08-21-portage-indicateurs-atas-design.md`

## Contraintes globales

Elles s'appliquent à **toutes** les tâches, implicitement.

- **Les fichiers partagés restent en C# 9 / `net48`.** Pas de namespace à portée de fichier, pas de
  `record`, pas de `required`, pas d'expression `switch` récente. Une construction trop moderne
  compile chez ATAS et **casse la compilation NinjaScript entière chez le trader** — indicateurs
  personnels compris, une compilation NinjaScript étant tout-ou-rien.
- **Les deux builds passent, jamais un seul.** À la fin de chaque tâche :
  ```bash
  dotnet build src/NinjaTrader.Scripts/NinjaTrader.Scripts.csproj -c Release
  dotnet build src/Atas.Indicators.TradeDeck/Atas.Indicators.TradeDeck.csproj -c Release
  ```
- **~180 avertissements `CS0436` côté NinjaTrader sont normaux** (types déjà présents dans
  `NinjaTrader.Custom.dll`). Ne pas chercher à les corriger.
- **Namespace des fichiers partagés : `TradeDeck.Analytics`.**
- **Les commentaires expliquent le *pourquoi*, en anglais**, comme le reste du code. Ceux qui
  existent déjà devant un garde-fou ne se suppriment pas : la raison est presque toujours un
  incident réel.
- **Aucun test automatisé n'existe dans ce dépôt et ce plan n'en introduit pas.** La vérification
  de chaque tâche est une compilation plus un contrôle décrit explicitement.
- **`decimal` côté ATAS, `double` dans les fichiers partagés.** La conversion se fait aux bornes.
- Les artefacts de build (`bin/`, `obj/`) sont versionnés dans ce dépôt : leur apparition dans
  `git status` est attendue, ne pas « nettoyer ».

## Structure des fichiers

| Fichier | Responsabilité |
|---|---|
| `src/NinjaTrader.Scripts/Indicators/TdSwingEngine.cs` | **partagé** — détection des pivots (existe, namespace à changer) |
| `src/NinjaTrader.Scripts/Indicators/TdStopSelector.cs` | **partagé** — couloir de distance (à créer, extrait) |
| `src/NinjaTrader.Scripts/Indicators/TdSwingStructure.cs` | NinjaTrader — affichage (un `using` à ajouter) |
| `src/NinjaTrader.Scripts/Indicators/TdStopPlacement.cs` | NinjaTrader — affichage (allégé du couloir) |
| `src/NinjaTrader.AddOn.StreamDeck/Services/TrendEngine.cs` | add-on — un `using` à ajouter |
| `src/Atas.Indicators.TradeDeck/Atas.Indicators.TradeDeck.csproj` | projet ATAS et liens partagés |
| `src/Atas.Indicators.TradeDeck/TdSwingStructure.cs` | ATAS — affichage des pivots |
| `src/Atas.Indicators.TradeDeck/TdStopPlacement.cs` | ATAS — affichage du stop |
| `src/Atas.Indicators.TradeDeck/StatsBox.cs` | ATAS — l'encart de statistiques, partagé par les deux indicateurs |

---

## Tâche 1 : extraire `TdStopSelector` et neutraliser le namespace

Tâche **entièrement NinjaTrader**, sans une ligne d'ATAS. C'est un remaniement à comportement
constant : à la fin, les deux indicateurs NinjaTrader doivent produire exactement les mêmes points
qu'avant.

**Fichiers :**
- Créer : `src/NinjaTrader.Scripts/Indicators/TdStopSelector.cs`
- Modifier : `src/NinjaTrader.Scripts/Indicators/TdSwingEngine.cs` (namespace)
- Modifier : `src/NinjaTrader.Scripts/Indicators/TdSwingStructure.cs` (un `using`)
- Modifier : `src/NinjaTrader.Scripts/Indicators/TdStopPlacement.cs` (`using` + délégation)
- Modifier : `src/NinjaTrader.AddOn.StreamDeck/Services/TrendEngine.cs` (un `using`)

**Interfaces :**
- Produit : `TradeDeck.Analytics.TdSwingEngine`, `TradeDeck.Analytics.TdSwingPivot`,
  `TradeDeck.Analytics.TdStopSelector.TrySelectAnchor(IList<TdSwingPivot> pivots, int currentBar,
  bool isLong, double entry, double minDistance, double maxDistance, int lookbackPivots,
  bool skipMostRecent, out double anchor) → bool`

- [ ] **Étape 1 : changer le namespace du moteur**

Dans `src/NinjaTrader.Scripts/Indicators/TdSwingEngine.cs`, remplacer la ligne de commentaire et la
déclaration de namespace :

```csharp
// This namespace holds Indicators in this folder and is required. Do not change it.
namespace NinjaTrader.NinjaScript.Indicators
```

par :

```csharp
// Deliberately NOT NinjaTrader.NinjaScript.Indicators. This file is compiled by BOTH NinjaScript
// and the ATAS project (see src/Atas.Indicators.TradeDeck), and a NinjaTrader.* namespace inside an
// ATAS assembly makes the next reader doubt what they are looking at. NinjaScript compiles every
// .cs under bin\Custom into one assembly and places no constraint on the namespace of a plain
// class — only indicators need theirs.
namespace TradeDeck.Analytics
```

- [ ] **Étape 2 : créer `TdStopSelector.cs`**

Créer `src/NinjaTrader.Scripts/Indicators/TdStopSelector.cs` :

```csharp
#region Using declarations
using System.Collections.Generic;
#endregion

namespace TradeDeck.Analytics
{
    /// <summary>
    /// Chooses which confirmed pivot a stop belongs behind. Extracted from TdStopPlacement so that
    /// NinjaTrader and ATAS share the selection itself rather than two drifting copies of it.
    ///
    /// A CORRIDOR, NOT A REGIME CLASSIFIER. The natural framing is "detect the regime, then pick a
    /// major or a minor pivot". That is the wrong cut: a regime classifier flips exactly at the
    /// transitions, which is the worst possible moment, and the label is only ever a proxy for what
    /// actually matters — DISTANCE. Filtering candidates by a distance corridor makes the
    /// major/minor choice fall out on its own: in a sustained trend the pullback lows sit close, so
    /// the nearest one above the floor is usually a minor pivot; in a range the minor pivots are
    /// glued to price, drop below the floor, are eliminated, and the major pivot wins.
    ///
    /// WHY THE MOST RECENT PIVOT IS SKIPPED. Not merely because it is close. It is the most OBVIOUS
    /// level on the chart, so it is where every stop is parked, so it is where the sweep goes.
    /// Hard-coding "always take the second to last" would be wrong the other way, taking a
    /// needlessly wide stop whenever the last low is already deep — hence: step past the obvious
    /// level ONLY when another valid candidate exists.
    ///
    /// NO LOOK-AHEAD. Candidates are filtered on ConfirmationBar, never on the bar carrying the
    /// extreme. Reading the last low off the finished chart would quietly read the future.
    ///
    /// Distances are passed in PRICE UNITS, already scaled by the caller — like TdSwingEngine, this
    /// class holds no opinion on ATR, ticks or regime.
    /// </summary>
    public class TdStopSelector
    {
        /// <summary>Reused per call so the scan does not allocate on every bar.</summary>
        private readonly List<TdSwingPivot> _candidates = new List<TdSwingPivot>();

        /// <returns>False when nothing valid was found, which is the caller's cue to fall back.</returns>
        public bool TrySelectAnchor(
            IList<TdSwingPivot> pivots,
            int currentBar,
            bool isLong,
            double entry,
            double minDistance,
            double maxDistance,
            int lookbackPivots,
            bool skipMostRecent,
            out double anchor)
        {
            anchor = 0.0;
            _candidates.Clear();

            int inspected = 0;

            // Backwards, so index 0 of _candidates is the most recent qualifying level.
            for (int i = pivots.Count - 1; i >= 0 && inspected < lookbackPivots; i--)
            {
                var pivot = pivots[i];

                // A long is protected by a swing LOW, a short by a swing HIGH.
                if (pivot.IsHigh == isLong)
                    continue;

                inspected++;

                // Only levels already knowable at this bar. The engine appends on confirmation, so
                // this holds by construction — the test guards against a future change of order.
                if (pivot.ConfirmationBar > currentBar)
                    continue;

                double distance = isLong ? entry - pivot.Price : pivot.Price - entry;
                if (distance <= 0.0)
                    continue;
                if (distance < minDistance || distance > maxDistance)
                    continue;

                _candidates.Add(pivot);
            }

            if (_candidates.Count == 0)
                return false;

            // Step past the obvious level only when there is somewhere else to go.
            int start = skipMostRecent && _candidates.Count >= 2 ? 1 : 0;

            double best = 0.0;
            double bestDistance = double.MaxValue;
            for (int i = start; i < _candidates.Count; i++)
            {
                var pivot = _candidates[i];
                double distance = isLong ? entry - pivot.Price : pivot.Price - entry;
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = pivot.Price;
                }
            }

            anchor = best;
            return true;
        }
    }
}
```

- [ ] **Étape 3 : brancher les trois consommateurs NinjaTrader**

Dans `src/NinjaTrader.Scripts/Indicators/TdSwingStructure.cs`, ajouter à la fin du bloc
`#region Using declarations` :

```csharp
using TradeDeck.Analytics;
```

Idem dans `src/NinjaTrader.Scripts/Indicators/TdStopPlacement.cs`.

Dans `src/NinjaTrader.AddOn.StreamDeck/Services/TrendEngine.cs`, ajouter la même ligne au bloc de
`using` en tête de fichier.

- [ ] **Étape 4 : faire déléguer `TdStopPlacement`**

Dans `src/NinjaTrader.Scripts/Indicators/TdStopPlacement.cs`, remplacer le champ :

```csharp
        /// <summary>Reused per bar so the selection does not allocate on every candidate scan.</summary>
        private readonly List<TdSwingPivot> _candidates = new List<TdSwingPivot>();
```

par :

```csharp
        private readonly TdStopSelector _selector = new TdStopSelector();
```

Supprimer intégralement la méthode privée `TrySelectAnchor` (de son commentaire `<summary>` jusqu'à
son accolade fermante), et remplacer son unique appel dans `OnBarUpdate` :

```csharp
            if (TrySelectAnchor(isLong, entry, atr, out anchor))
```

par :

```csharp
            if (_selector.TrySelectAnchor(
                    _engine.Pivots, CurrentBar, isLong, entry,
                    MinDistanceAtr * atr, MaxDistanceAtr * atr,
                    LookbackPivots, SkipMostRecentPivot, out anchor))
```

- [ ] **Étape 5 : vérifier que les deux projets NinjaTrader compilent**

```bash
dotnet build src/NinjaTrader.Scripts/NinjaTrader.Scripts.csproj -c Release
dotnet build src/NinjaTrader.AddOn.StreamDeck/NinjaTrader.AddOn.StreamDeck.csproj -c Release
```

Attendu : `Build succeeded` pour les deux, avec des `CS0436` en avertissement.
Si `CS0246: TdSwingPivot introuvable` apparaît, un `using TradeDeck.Analytics;` a été oublié —
l'étape 3 en pose trois.

- [ ] **Étape 6 : vérifier que le comportement n'a pas changé**

C'est un remaniement, donc la seule preuve valable est l'identité des sorties.

1. Avant de déployer, noter les statistiques imprimées par `TdStopPlacement` sur un graphique de
   référence (200 ticks, une journée complète) dans l'installation NinjaTrader actuelle : ligne
   « % niveau structurel / % repli ATR » et « largeur du stop ».
2. Copier les sources modifiées, **NinjaTrader ouvert** — il surveille `bin\Custom` et recompile
   seul :
   ```powershell
   $nt = "$env:USERPROFILE\Documents\NinjaTrader 8\bin\Custom"
   Copy-Item "src\NinjaTrader.Scripts\Indicators\*.cs" "$nt\Indicators\" -Force
   ```
3. Recharger le même graphique, fermer l'indicateur, relire les statistiques.

Attendu : **des nombres identiques au dixième près.** Un écart signifie que l'extraction a changé le
comportement — la cause la plus probable est l'ordre des paramètres `minDistance` / `maxDistance`.

- [ ] **Étape 7 : commit**

```bash
git add src/NinjaTrader.Scripts/Indicators/TdStopSelector.cs \
        src/NinjaTrader.Scripts/Indicators/TdSwingEngine.cs \
        src/NinjaTrader.Scripts/Indicators/TdSwingStructure.cs \
        src/NinjaTrader.Scripts/Indicators/TdStopPlacement.cs \
        src/NinjaTrader.AddOn.StreamDeck/Services/TrendEngine.cs
git commit -m "Extraire TdStopSelector et neutraliser le namespace partage

Le couloir de distance quitte TdStopPlacement pour une classe ordinaire, sur
le modele de TdSwingEngine : c est le second algorithme pur du projet, et il
va etre partage avec ATAS plutot que reecrit.

Namespace TradeDeck.Analytics et non NinjaTrader.NinjaScript.Indicators : ces
fichiers seront compiles dans un assembly ATAS, ou un namespace NinjaTrader
n aurait aucun sens.

Remaniement a comportement constant : memes statistiques avant et apres sur le
graphique de reference."
```

---

## Tâche 2 : le projet ATAS, les liens partagés, un premier indicateur chargé

Objectif : **prouver le mécanisme** avant d'écrire le moindre algorithme. À la fin de cette tâche,
un indicateur trivial s'affiche dans ATAS X, et l'assembly contient déjà les deux classes partagées.

**Fichiers :**
- Créer : `src/Atas.Indicators.TradeDeck/Atas.Indicators.TradeDeck.csproj`
- Créer : `src/Atas.Indicators.TradeDeck/TdSwingStructure.cs` (version minimale)

**Interfaces :**
- Consomme : `TradeDeck.Analytics.TdSwingEngine`, `TradeDeck.Analytics.TdStopSelector` (tâche 1)
- Produit : l'assembly `TradeDeck.Indicators.dll`, namespace `TradeDeck.Atas.Indicators`

- [ ] **Étape 1 : créer le `.csproj`**

Créer `src/Atas.Indicators.TradeDeck/Atas.Indicators.TradeDeck.csproj` :

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <!--
    ATAS X uniquement. Son runtimeconfig.json annonce net10.0, et son dossier d'installation est
    `C:\Program Files\ATAS X` — a ne pas confondre avec l'ATAS classique, en `Program Files (x86)`,
    qui est en net10.0-windows et WPF. Ajouter la cible classique plus tard sera un changement de
    ce fichier et non de code, A CONDITION de n'introduire aucun type WPF : ATAS X ne sait pas
    convertir un editeur WPF personnalise.
  -->
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <LangVersion>latest</LangVersion>
    <Nullable>annotations</Nullable>
    <UseWPF>false</UseWPF>
    <RootNamespace>TradeDeck.Atas.Indicators</RootNamespace>
    <AssemblyName>TradeDeck.Indicators</AssemblyName>
    <!-- Les indicateurs ATAS dessinent via System.Drawing : CA1416 est attendu, comme chez eux. -->
    <NoWarn>$(NoWarn);CA1416</NoWarn>
  </PropertyGroup>

  <PropertyGroup>
    <ATAS_BASE Condition="'$(ATAS_BASE)' == ''">C:\Program Files\ATAS X</ATAS_BASE>
  </PropertyGroup>

  <!--
    LES ALGORITHMES NE SONT PAS COPIES ICI, ILS SONT LUS LA-BAS.
    Un seul fichier sur le disque, deux compilateurs. Ces fichiers sont aussi compiles par
    NinjaScript chez le trader : ils doivent rester en C# 9 / net48. Une construction plus recente
    compile ici et casse la compilation NinjaScript ENTIERE chez lui, indicateurs personnels
    compris. Le garde-fou est `dotnet build src/NinjaTrader.Scripts` : les deux passent, ou aucun.
  -->
  <ItemGroup>
    <Compile Include="..\NinjaTrader.Scripts\Indicators\TdSwingEngine.cs"  Link="Shared\TdSwingEngine.cs" />
    <Compile Include="..\NinjaTrader.Scripts\Indicators\TdStopSelector.cs" Link="Shared\TdStopSelector.cs" />
  </ItemGroup>

  <ItemGroup>
    <Reference Include="ATAS.Indicators">
      <HintPath>$(ATAS_BASE)\ATAS.Indicators.dll</HintPath>
      <Private>false</Private>
    </Reference>
    <Reference Include="ATAS.DataFeedsCore">
      <HintPath>$(ATAS_BASE)\ATAS.DataFeedsCore.dll</HintPath>
      <Private>false</Private>
    </Reference>
    <Reference Include="OFT.Rendering">
      <HintPath>$(ATAS_BASE)\OFT.Rendering.dll</HintPath>
      <Private>false</Private>
    </Reference>
    <Reference Include="OFT.Attributes">
      <HintPath>$(ATAS_BASE)\OFT.Attributes.dll</HintPath>
      <Private>false</Private>
    </Reference>
    <Reference Include="OFT.Localization">
      <HintPath>$(ATAS_BASE)\OFT.Localization.dll</HintPath>
      <Private>false</Private>
    </Reference>
    <Reference Include="Utils.Common">
      <HintPath>$(ATAS_BASE)\Utils.Common.dll</HintPath>
      <Private>false</Private>
    </Reference>
  </ItemGroup>

</Project>
```

- [ ] **Étape 2 : créer l'indicateur minimal**

Créer `src/Atas.Indicators.TradeDeck/TdSwingStructure.cs`. À ce stade, **l'EMA seule** — juste de
quoi prouver que l'indicateur se charge, que les réglages apparaissent et que le tracé sort :

```csharp
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;

using ATAS.Indicators;
using ATAS.Indicators.Drawing;
using ATAS.Indicators.Technical;

using OFT.Attributes;

namespace TradeDeck.Atas.Indicators;

[DisplayName("TD Swing Structure")]
[Display(Name = "TD Swing Structure",
    Description = "Creux et sommets structurels, seuil normalise par l'ATR. Detection seule, aucun ordre.")]
public class TdSwingStructure : Indicator
{
    private readonly EMA _ema = new() { Period = 40 };

    // ShowZeroValue = false remplace le double.NaN de NinjaTrader : decimal n'a pas de NaN, et une
    // serie decimal s'initialise a 0. Sans cette propriete, chaque barre sans valeur tracerait un
    // point a 0 et ecraserait l'echelle du graphique sur l'axe.
    private readonly ValueDataSeries _emaSeries = new("Ema", "EMA")
    {
        Color = DefaultColors.Blue.Convert(),
        VisualType = VisualMode.Line,
        Width = 2,
        ShowZeroValue = false
    };

    // Pas de `Panel = ...` : la valeur par defaut est le panneau des bougies, c'est-a-dire
    // l'equivalent de IsOverlay = true chez NinjaTrader.
    public TdSwingStructure() : base(true)
    {
        DataSeries[0] = _emaSeries;
        Add(_ema);
    }

    [Parameter]
    [Display(Name = "Periode EMA", GroupName = "Affichage", Order = 30)]
    [Range(1, 10000)]
    public int EmaPeriod
    {
        get => _ema.Period;
        // RecalculateValues() est OBLIGATOIRE : sans lui, changer le reglage ne relance aucun
        // calcul et l'indicateur ment silencieusement.
        set { _ema.Period = value; RecalculateValues(); }
    }

    protected override void OnCalculate(int bar, decimal value)
    {
        _emaSeries[bar] = _ema[bar];
    }
}
```

- [ ] **Étape 3 : compiler**

```bash
dotnet build src/Atas.Indicators.TradeDeck/Atas.Indicators.TradeDeck.csproj -c Release
```

Attendu : `Build succeeded`.
Si `MSB3245: impossible de resoudre la reference ATAS.Indicators`, ATAS X n'est pas dans
`C:\Program Files\ATAS X` — passer le chemin réel :
`dotnet build ... -p:ATAS_BASE="<chemin>"`.

- [ ] **Étape 4 : vérifier que le lien partagé a bien fonctionné**

C'est le point entier de la tâche, et un `Build succeeded` ne le prouve pas : les classes partagées
pourraient être absentes sans que rien n'échoue.

```bash
dotnet build src/Atas.Indicators.TradeDeck/Atas.Indicators.TradeDeck.csproj -c Release -v n 2>&1 | grep -i "TdSwingEngine\|TdStopSelector"
```

Attendu : les deux fichiers apparaissent dans la liste des sources compilées. S'ils manquent, le
chemin relatif du `<Compile Include>` est faux.

- [ ] **Étape 5 : vérifier que les deux builds passent toujours**

```bash
dotnet build src/NinjaTrader.Scripts/NinjaTrader.Scripts.csproj -c Release
```

Attendu : `Build succeeded`. Cette commande est le garde-fou C# 9 — elle doit être relancée après
**chaque** modification d'un fichier partagé, à toutes les tâches suivantes.

- [ ] **Étape 6 : charger dans ATAS X**

```powershell
Copy-Item "src\Atas.Indicators.TradeDeck\bin\Release\net10.0\TradeDeck.Indicators.dll" `
          "$env:APPDATA\ATAS X\Indicators\" -Force
```

Dans ATAS X : ouvrir un graphique, ajouter un indicateur, chercher « TD Swing Structure ».

Attendu : l'indicateur apparaît dans la liste, se pose sur le graphique, trace une EMA bleue, et son
panneau de réglages montre « Periode EMA » sous le groupe « Affichage ». Changer la période
redessine immédiatement.

Si l'indicateur n'apparaît pas dans la liste, utiliser le bouton « ajouter un indicateur
personnalisé » et désigner le DLL directement — il charge sans redémarrer.

- [ ] **Étape 7 : commit**

```bash
git add src/Atas.Indicators.TradeDeck/
git commit -m "Creer le projet ATAS et prouver le lien de source partagee

Un indicateur minimal, l EMA seule, qui se charge dans ATAS X. L interet n est
pas l EMA : c est que TdSwingEngine et TdStopSelector sont desormais compiles
par les DEUX chaines depuis un seul fichier, et que le garde-fou C# 9 de
NinjaTrader.Scripts passe toujours.

net10.0 sans WPF, DLL referencees depuis C:\\Program Files\\ATAS X."
```

---

## Tâche 3 : `TdSwingStructure` — le séquencement de barre close et les pivots

Le cœur du portage. C'est ici que se joue la seule erreur capable de produire du code qui marche et
des résultats faux.

**Fichiers :**
- Modifier : `src/Atas.Indicators.TradeDeck/TdSwingStructure.cs`

**Interfaces :**
- Consomme : `TdSwingEngine(int maxStoredPivots)`, `.Update(int bar, double high, double low,
  double majorThreshold, double minorThreshold, int minBarsBetweenPivots)`, `.JustConfirmed`,
  `.Pivots`, et les champs de `TdSwingPivot` (`ExtremeBar`, `ConfirmationBar`, `Price`, `IsHigh`,
  `IsMajor`, `HasPrevious`, `PreviousExtremeBar`, `PreviousPrice`)
- Produit : `TdSwingStructure` complet, moins l'encart de statistiques (tâche 4)

- [ ] **Étape 1 : remplacer le contenu du fichier**

Remplacer intégralement `src/Atas.Indicators.TradeDeck/TdSwingStructure.cs` :

```csharp
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;

using ATAS.Indicators;
using ATAS.Indicators.Drawing;
using ATAS.Indicators.Technical;

using OFT.Attributes;

using TradeDeck.Analytics;

namespace TradeDeck.Atas.Indicators;

/// <summary>
/// Plots structural swing highs and lows as dots. Detection itself lives in TdSwingEngine, which is
/// the SAME FILE the NinjaTrader build compiles — see the csproj.
///
/// WHY NOT FRACTALS. The obvious detector — "a high greater than the n bars either side" — is a
/// purely POSITIONAL test. It carries no notion of how far price actually travelled, so in a range
/// it validates every micro-oscillation, and in a strong trend it misses the short pullbacks
/// because n is fixed. Market structure is a question of DISTANCE, not of bar count.
///
/// WHY ATR AND NOT TICKS OR PERCENT. A threshold in ticks has to be recalibrated for every
/// instrument and again whenever volatility changes; a percentage is meaningless on futures.
///
/// PIVOTS ARE CONFIRMED LATE, BY DEFINITION. A high is only known to have been a high once price
/// has come back down from it. Live, the dot appears some bars after the extreme it marks. Nothing
/// built on these pivots may assume one was known at the time it formed.
/// </summary>
[DisplayName("TD Swing Structure")]
[Display(Name = "TD Swing Structure",
    Description = "Creux et sommets structurels, seuil normalise par l'ATR. Detection seule, aucun ordre.")]
public class TdSwingStructure : Indicator
{
    /// <summary>
    /// Floor applied to the threshold, in ticks. On a dead series the ATR collapses and every bar
    /// would confirm a pivot; two ticks is the smallest move that can mean anything.
    /// </summary>
    private const decimal MinimumThresholdTicks = 2m;

    private const int MaxStoredPivots = 512;

    private readonly ATR _atr = new() { Period = 20 };
    private readonly EMA _ema = new() { Period = 40 };

    private readonly ValueDataSeries _swingHigh = new("SwingHigh", "Sommet structurel")
    {
        Color = DefaultColors.Red.Convert(), VisualType = VisualMode.Dots, Width = 4, ShowZeroValue = false
    };
    private readonly ValueDataSeries _swingLow = new("SwingLow", "Creux structurel")
    {
        Color = DefaultColors.Green.Convert(), VisualType = VisualMode.Dots, Width = 4, ShowZeroValue = false
    };
    private readonly ValueDataSeries _minorHigh = new("MinorHigh", "Sommet intermediaire")
    {
        Color = System.Drawing.Color.IndianRed.Convert(), VisualType = VisualMode.Dots, Width = 2, ShowZeroValue = false
    };
    private readonly ValueDataSeries _minorLow = new("MinorLow", "Creux intermediaire")
    {
        Color = System.Drawing.Color.MediumSeaGreen.Convert(), VisualType = VisualMode.Dots, Width = 2, ShowZeroValue = false
    };
    private readonly ValueDataSeries _emaSeries = new("Ema", "EMA")
    {
        Color = DefaultColors.Blue.Convert(), VisualType = VisualMode.Line, Width = 2, ShowZeroValue = false
    };

    private TdSwingEngine _engine = new(MaxStoredPivots);

    /// <summary>Last bar index seen by OnCalculate. -1 means "nothing processed yet".</summary>
    private int _lastBar = -1;

    // Session statistics. Rendered by the box in TdSwingStructure.OnRender (task 4).
    private readonly List<double> _legTicks = new();
    private readonly List<double> _legBars = new();
    private int _majorHighs;
    private int _majorLows;
    private int _minorPivots;

    private decimal _majorThresholdAtr = 2.5m;
    private decimal _minorThresholdAtr = 1.0m;
    private bool _showMinorPivots = true;
    private int _minBarsBetweenPivots = 3;
    private int _dotOffsetTicks = 4;
    private bool _showEma = true;

    public TdSwingStructure() : base(true)
    {
        DataSeries[0] = _swingHigh;
        DataSeries.Add(_swingLow);
        DataSeries.Add(_minorHigh);
        DataSeries.Add(_minorLow);
        DataSeries.Add(_emaSeries);

        Add(_atr);
        Add(_ema);
    }

    protected override void OnRecalculate()
    {
        _engine = new TdSwingEngine(MaxStoredPivots);
        _lastBar = -1;
        _legTicks.Clear();
        _legBars.Clear();
        _majorHighs = 0;
        _majorLows = 0;
        _minorPivots = 0;
        DataSeries.ForEach(x => x.Clear());
    }

    protected override void OnCalculate(int bar, decimal value)
    {
        // THE ONE THING THIS PORT COULD GET WRONG.
        //
        // NinjaTrader's Calculate.OnBarClose called OnBarUpdate exactly once, on a bar that had
        // closed. ATAS re-enters OnCalculate on EVERY TICK of the forming bar. TdSwingEngine is a
        // state machine that mutates on every call, so two distinct corruptions follow: feeding the
        // same bar twice breaks the alternation, and feeding a bar that is still forming anchors a
        // running extreme on a partial High that later grows.
        //
        // So: advance only when the index moves, and feed the bar that has just CLOSED. ATAS's own
        // EMA uses `CurrentBar - 1` the same way for its alerts.
        //
        // Side effect, and a welcome one: the historical pass feeds bars 0..CurrentBar-1 and leaves
        // the forming bar alone — exactly NinjaTrader's behaviour, for free.
        if (bar == _lastBar)
            return;
        _lastBar = bar;

        if (bar == 0)
            return;

        var closed = bar - 1;

        // The EMA is drawn from the first bar, before the ATR warm-up gate below — matching the
        // NinjaTrader version, which set the EMA plot before its own early return.
        _emaSeries[closed] = _showEma ? _ema[closed] : 0m;

        // The ATR needs its window before the threshold means anything. Seeding the engine earlier
        // would anchor it on a value computed from almost no data.
        if (closed < _atr.Period + 1)
            return;

        var tick = InstrumentInfo?.TickSize ?? 0m;
        if (tick <= 0m)
            return;

        var unit = _atr[closed];
        if (unit < tick)
            unit = tick;

        var floor = MinimumThresholdTicks * tick;
        var candle = GetCandle(closed);

        // Both tiers are ALWAYS tracked. ShowMinorPivots governs the drawing only — the
        // intermediate pivots are what TdStopPlacement leans on in a trend, and a display toggle
        // silently emptying the history would be a trap.
        _engine.Update(
            closed,
            (double)candle.High,
            (double)candle.Low,
            (double)Math.Max(unit * _majorThresholdAtr, floor),
            (double)Math.Max(unit * _minorThresholdAtr, floor),
            _minBarsBetweenPivots);

        var confirmed = _engine.JustConfirmed;
        for (var i = 0; i < confirmed.Count; i++)
            PlotPivot(confirmed[i], tick);
    }

    private void PlotPivot(TdSwingPivot pivot, decimal tick)
    {
        if (!pivot.IsMajor && !_showMinorPivots)
            return;

        var offset = _dotOffsetTicks * tick;
        var price = (decimal)pivot.Price;

        // Absolute bar indexing: writing a pivot back into the bar that carried the extreme is
        // trivial here. NinjaTrader needed MaximumBarsLookBack.Infinite for the same write, because
        // its indexing is relative and the default 256-bar window swallowed it in silence.
        var target = pivot.IsHigh ? price + offset : price - offset;

        if (!pivot.IsMajor)
        {
            if (pivot.IsHigh)
                _minorHigh[pivot.ExtremeBar] = target;
            else
                _minorLow[pivot.ExtremeBar] = target;

            _minorPivots++;
            return;
        }

        if (pivot.IsHigh)
        {
            _swingHigh[pivot.ExtremeBar] = target;
            _majorHighs++;
        }
        else
        {
            _swingLow[pivot.ExtremeBar] = target;
            _majorLows++;
        }

        if (pivot.HasPrevious && tick > 0m)
        {
            _legTicks.Add(Math.Abs(pivot.Price - pivot.PreviousPrice) / (double)tick);
            _legBars.Add(pivot.ExtremeBar - pivot.PreviousExtremeBar);
        }
    }

    #region Reglages

    [Parameter]
    [Display(Name = "Periode ATR", Description = "Fenetre de l'ATR qui sert d'unite au seuil. Longue = seuil stable.", GroupName = "Detection", Order = 1)]
    [Range(2, 10000)]
    public int AtrPeriod
    {
        get => _atr.Period;
        set { _atr.Period = value; RecalculateValues(); }
    }

    [Parameter]
    [Display(Name = "Seuil structurel (x ATR)", Description = "Retracement minimal pour valider un pivot majeur.", GroupName = "Detection", Order = 2)]
    [Range(0.1, 1000)]
    public decimal MajorThresholdAtr
    {
        get => _majorThresholdAtr;
        set { _majorThresholdAtr = value; RecalculateValues(); }
    }

    [Parameter]
    [Display(Name = "Seuil intermediaire (x ATR)", Description = "Seuil des pivots mineurs : les pullbacks courts de tendance.", GroupName = "Detection", Order = 3)]
    [Range(0.1, 1000)]
    public decimal MinorThresholdAtr
    {
        get => _minorThresholdAtr;
        set { _minorThresholdAtr = value; RecalculateValues(); }
    }

    [Parameter]
    [Display(Name = "Afficher les pivots intermediaires", Description = "Affichage seul : les pivots mineurs restent detectes dans tous les cas.", GroupName = "Detection", Order = 4)]
    public bool ShowMinorPivots
    {
        get => _showMinorPivots;
        set { _showMinorPivots = value; RecalculateValues(); }
    }

    [Parameter]
    [Display(Name = "Barres minimum entre pivots", Description = "Anti-grappe : empeche deux pivots colles sur une secousse.", GroupName = "Detection", Order = 5)]
    [Range(1, 10000)]
    public int MinBarsBetweenPivots
    {
        get => _minBarsBetweenPivots;
        set { _minBarsBetweenPivots = value; RecalculateValues(); }
    }

    [Parameter]
    [Display(Name = "Decalage des points (ticks)", Description = "Ecart entre le point et la meche, pour la lisibilite.", GroupName = "Affichage", Order = 10)]
    [Range(0, 10000)]
    public int DotOffsetTicks
    {
        get => _dotOffsetTicks;
        set { _dotOffsetTicks = value; RecalculateValues(); }
    }

    [Parameter]
    [Display(Name = "Afficher l'EMA", Description = "Reference visuelle de tendance.", GroupName = "Affichage", Order = 20)]
    public bool ShowEma
    {
        get => _showEma;
        set { _showEma = value; RecalculateValues(); }
    }

    [Parameter]
    [Display(Name = "Periode EMA", GroupName = "Affichage", Order = 30)]
    [Range(1, 10000)]
    public int EmaPeriod
    {
        get => _ema.Period;
        set { _ema.Period = value; RecalculateValues(); }
    }

    #endregion
}
```

- [ ] **Étape 2 : compiler les deux projets**

```bash
dotnet build src/Atas.Indicators.TradeDeck/Atas.Indicators.TradeDeck.csproj -c Release
dotnet build src/NinjaTrader.Scripts/NinjaTrader.Scripts.csproj -c Release
```

Attendu : `Build succeeded` pour les deux.

- [ ] **Étape 3 : déployer et observer**

```powershell
Copy-Item "src\Atas.Indicators.TradeDeck\bin\Release\net10.0\TradeDeck.Indicators.dll" `
          "$env:APPDATA\ATAS X\Indicators\" -Force
```

Poser l'indicateur sur un graphique **200 ticks** d'un future liquide.

Attendu :
- de gros points rouges au-dessus des sommets, de gros points verts sous les creux ;
- des petits points atténués aux pivots intermédiaires ;
- une EMA bleue ;
- **aucun point à zéro**, et l'échelle du graphique inchangée. Si l'échelle s'écrase vers l'axe, un
  `ShowZeroValue = false` manque sur une série.

- [ ] **Étape 4 : vérifier le séquencement de barre close**

Le contrôle qui compte. En direct, marché ouvert, observer la barre en formation.

Attendu : **aucun point n'apparaît sur la barre en cours**, et aucun point déjà posé ne bouge quand
le prix évolue à l'intérieur de la barre. Un point qui saute d'une barre à l'autre pendant sa
formation signifie que le moteur est nourri de la barre ouverte — relire la garde `_lastBar`.

- [ ] **Étape 5 : commit**

```bash
git add src/Atas.Indicators.TradeDeck/TdSwingStructure.cs
git commit -m "Porter TdSwingStructure sur ATAS X

Le sequencement est le seul vrai piege : ATAS rappelle OnCalculate a chaque
tick, la ou Calculate.OnBarClose ne l appelait qu une fois par barre close. Le
moteur mute a chaque appel, donc on n avance que lorsque l index bouge et on
lui donne la barre qui vient de clore.

En echange, MaximumBarsLookBack disparait : l indexation absolue rend l
ecriture retroactive d un pivot triviale. Et ShowZeroValue = false remplace le
double.NaN, decimal n en ayant pas."
```

---

## Tâche 4 : l'encart de statistiques

ATAS n'a pas de fenêtre de sortie — son `Indicator` n'implémente pas `ILoggerSource`, il n'existe ni
`Print` ni `LogInfo`. Or la calibration en dépend : « régler `k` à l'œil sur trois journées est
lent ; avoir les chiffres à côté en fait une décision ».

**Fichiers :**
- Créer : `src/Atas.Indicators.TradeDeck/StatsBox.cs`
- Modifier : `src/Atas.Indicators.TradeDeck/TdSwingStructure.cs`

**Interfaces :**
- Produit : `StatsBox.Draw(RenderContext context, IIndicatorContainer container, IReadOnlyList<string> lines)`,
  `StatsBox.Mean(List<double>)`, `StatsBox.MedianOf(List<double>)`

- [ ] **Étape 1 : créer l'encart**

Créer `src/Atas.Indicators.TradeDeck/StatsBox.cs` :

```csharp
using System.Collections.Generic;
using System.Drawing;

using ATAS.Indicators;

using OFT.Rendering.Context;
using OFT.Rendering.Tools;

namespace TradeDeck.Atas.Indicators;

/// <summary>
/// The calibration figures, drawn in a corner of the chart.
///
/// NinjaTrader printed these to the output window on Terminated. ATAS has no output window at all,
/// and dropping the numbers would gut the indicator's actual purpose — they are what turns setting
/// the ATR multiple from an impression into a decision. Drawing them is also strictly better than
/// the original: they were only readable once the chart was CLOSED, and they are now live.
/// </summary>
internal static class StatsBox
{
    private const int Padding = 8;
    private const int Margin = 12;

    private static readonly RenderFont Font = new("Arial", 11);
    private static readonly Color Background = Color.FromArgb(200, 20, 25, 35);
    private static readonly Color Text = Color.FromArgb(220, 220, 220);
    private static readonly RenderPen Border = new(Color.Gray, 1);

    public static void Draw(RenderContext context, IIndicatorContainer container, IReadOnlyList<string> lines)
    {
        if (context == null || container == null || lines == null || lines.Count == 0)
            return;

        var lineHeight = context.MeasureString("A", Font).Height;

        var width = 0;
        foreach (var line in lines)
        {
            var w = (int)context.MeasureString(line, Font).Width;
            if (w > width)
                width = w;
        }

        var boxWidth = width + Padding * 2;
        var boxHeight = lineHeight * lines.Count + Padding * 2;

        var x = container.Region.Width - boxWidth - Margin;
        var y = Margin;
        if (x < 0)
            x = 0;

        var rectangle = new Rectangle(x, y, boxWidth, boxHeight);
        context.FillRectangle(Background, rectangle);
        context.DrawRectangle(Border, rectangle);

        var currentY = y + Padding;
        foreach (var line in lines)
        {
            context.DrawString(line, Font, Text, x + Padding, currentY);
            currentY += lineHeight;
        }
    }

    public static double Mean(List<double> values)
    {
        if (values.Count == 0)
            return 0.0;

        var sum = 0.0;
        for (var i = 0; i < values.Count; i++)
            sum += values[i];
        return sum / values.Count;
    }

    /// <summary>Named MedianOf, not Median, to mirror the NinjaTrader side where the base class
    /// already exposes a MEDIAN accessor.</summary>
    public static double MedianOf(List<double> values)
    {
        if (values.Count == 0)
            return 0.0;

        var copy = new List<double>(values);
        copy.Sort();
        var middle = copy.Count / 2;
        return copy.Count % 2 == 1 ? copy[middle] : (copy[middle - 1] + copy[middle]) / 2.0;
    }
}
```

- [ ] **Étape 2 : brancher l'encart dans `TdSwingStructure`**

Dans `src/Atas.Indicators.TradeDeck/TdSwingStructure.cs`, ajouter aux `using` :

```csharp
using System.Globalization;

using OFT.Rendering.Context;
```

Ajouter le champ, avec les autres champs de réglage :

```csharp
    private bool _showStatistics = true;
```

Ajouter le réglage, à la fin de la région `Reglages` :

```csharp
    [Parameter]
    [Display(Name = "Afficher les statistiques", Description = "Encart de calibration : nombre de pivots, amplitude et duree des vagues.", GroupName = "Affichage", Order = 40)]
    public bool ShowStatistics
    {
        get => _showStatistics;
        set { _showStatistics = value; RedrawChart(); }
    }
```

Ajouter la méthode de rendu, après `PlotPivot` :

```csharp
    protected override void OnRender(RenderContext context, DrawingLayouts layout)
    {
        if (!_showStatistics || ChartInfo == null || Container == null)
            return;

        if (_majorHighs + _majorLows == 0)
            return;

        var lines = new List<string>
        {
            string.Format(CultureInfo.InvariantCulture,
                "pivots majeurs : {0} sommets / {1} creux — mineurs : {2}",
                _majorHighs, _majorLows, _minorPivots)
        };

        if (_legTicks.Count > 0)
        {
            lines.Add(string.Format(CultureInfo.InvariantCulture,
                "vagues : {0} — amplitude moy. {1:F1} ticks, med. {2:F1} ticks",
                _legTicks.Count, StatsBox.Mean(_legTicks), StatsBox.MedianOf(_legTicks)));
        }

        if (_legBars.Count > 0)
        {
            lines.Add(string.Format(CultureInfo.InvariantCulture,
                "duree : moy. {0:F1} barres, med. {1:F1} barres",
                StatsBox.Mean(_legBars), StatsBox.MedianOf(_legBars)));
        }

        StatsBox.Draw(context, Container, lines);
    }
```

- [ ] **Étape 3 : compiler les deux projets**

```bash
dotnet build src/Atas.Indicators.TradeDeck/Atas.Indicators.TradeDeck.csproj -c Release
dotnet build src/NinjaTrader.Scripts/NinjaTrader.Scripts.csproj -c Release
```

Attendu : `Build succeeded` pour les deux.
Si `CS0117: RedrawChart introuvable`, remplacer l'appel par `RecalculateValues()` — l'effet visuel
est le même, au prix d'un recalcul.

- [ ] **Étape 4 : déployer et vérifier**

Redéployer le DLL et recharger l'indicateur.

Attendu : un encart sombre en haut à droite, trois lignes, des nombres qui augmentent à mesure que
le graphique se remplit. Décocher « Afficher les statistiques » le fait disparaître.

- [ ] **Étape 5 : commit**

```bash
git add src/Atas.Indicators.TradeDeck/StatsBox.cs src/Atas.Indicators.TradeDeck/TdSwingStructure.cs
git commit -m "Afficher les statistiques de calibration dans un encart

ATAS n a pas de fenetre de sortie : son Indicator n implemente pas
ILoggerSource, il n existe ni Print ni LogInfo. Les chiffres passent donc dans
OnRender. C est meilleur que l original, qui ne les livrait qu a la fermeture
du graphique.

Ils servent aussi de recette au portage : memes nombres des deux cotes sur la
meme session, ou quelque chose est faux."
```

---

## Tâche 5 : `TdStopPlacement` — le couloir et le repli

**Fichiers :**
- Créer : `src/Atas.Indicators.TradeDeck/TdStopPlacement.cs`

**Interfaces :**
- Consomme : `TdSwingEngine`, `TdStopSelector.TrySelectAnchor(...)` (tâche 1), `StatsBox` (tâche 4)
- Produit : `TdStopPlacement`, moins la notation du balayage (tâche 6)

- [ ] **Étape 1 : créer le fichier**

Créer `src/Atas.Indicators.TradeDeck/TdStopPlacement.cs` :

```csharp
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;

using ATAS.Indicators;
using ATAS.Indicators.Drawing;
using ATAS.Indicators.Technical;

using OFT.Attributes;

using TradeDeck.Analytics;

namespace TradeDeck.Atas.Indicators;

/// <summary>Which side the preview assumes an entry was taken on.</summary>
public enum TdStopPreviewDirection
{
    /// <summary>Long above the EMA, short below it. Matches how the trade would actually be taken.</summary>
    SuivreEma,
    Achat,
    Vente
}

/// <summary>
/// Chooses where a stop loss belongs, and previews it on every bar so the choice can be judged
/// before a single order exists. The selection itself lives in TdStopSelector — the same file the
/// NinjaTrader build compiles.
///
/// IT PLACES NO ORDERS. This is a measuring instrument: a stop is computed for a hypothetical entry
/// at the close of every bar, plotted, and scored.
/// </summary>
[DisplayName("TD Stop Placement")]
[Display(Name = "TD Stop Placement",
    Description = "Choisit un stop structurel par couloir de distance, avec repli ATR. Apercu et mesure, aucun ordre.")]
public class TdStopPlacement : Indicator
{
    private const decimal MinimumThresholdTicks = 2m;
    private const int MaxStoredPivots = 512;

    private readonly ATR _atr = new() { Period = 20 };
    private readonly EMA _ema = new() { Period = 40 };

    private readonly ValueDataSeries _structural = new("StopStructurel", "Stop structurel")
    {
        Color = DefaultColors.Orange.Convert(), VisualType = VisualMode.Line, Width = 2, ShowZeroValue = false
    };
    private readonly ValueDataSeries _fallback = new("StopRepliAtr", "Stop de repli ATR")
    {
        Color = System.Drawing.Color.DimGray.Convert(), VisualType = VisualMode.Line, Width = 2, ShowZeroValue = false
    };
    private readonly ValueDataSeries _anchor = new("NiveauRetenu", "Niveau retenu")
    {
        Color = System.Drawing.Color.Gold.Convert(), VisualType = VisualMode.Dots, Width = 3, ShowZeroValue = false
    };

    private TdSwingEngine _engine = new(MaxStoredPivots);
    private readonly TdStopSelector _selector = new();
    private int _lastBar = -1;

    private readonly List<double> _distanceTicks = new();
    private readonly List<double> _distanceAtr = new();
    private int _structuralCount;
    private int _fallbackCount;

    private decimal _majorThresholdAtr = 2.5m;
    private decimal _minorThresholdAtr = 1.0m;
    private int _minBarsBetweenPivots = 3;
    private decimal _minDistanceAtr = 1.0m;
    private decimal _maxDistanceAtr = 3.0m;
    private int _bufferTicks = 4;
    private bool _skipMostRecentPivot = true;
    private int _lookbackPivots = 12;
    private decimal _fallbackAtrMultiple = 2.0m;
    private TdStopPreviewDirection _previewDirection = TdStopPreviewDirection.SuivreEma;

    public TdStopPlacement() : base(true)
    {
        DataSeries[0] = _structural;
        DataSeries.Add(_fallback);
        DataSeries.Add(_anchor);

        Add(_atr);
        Add(_ema);
    }

    protected override void OnRecalculate()
    {
        _engine = new TdSwingEngine(MaxStoredPivots);
        _lastBar = -1;
        _distanceTicks.Clear();
        _distanceAtr.Clear();
        _structuralCount = 0;
        _fallbackCount = 0;
        DataSeries.ForEach(x => x.Clear());
    }

    protected override void OnCalculate(int bar, decimal value)
    {
        // Same bar-close discipline as TdSwingStructure, and for the same reason: the engine is a
        // state machine, and ATAS re-enters OnCalculate on every tick of the forming bar.
        if (bar == _lastBar)
            return;
        _lastBar = bar;

        if (bar == 0)
            return;

        var closed = bar - 1;

        if (closed < _atr.Period + 1)
            return;

        var tick = InstrumentInfo?.TickSize ?? 0m;
        if (tick <= 0m)
            return;

        var atr = _atr[closed];
        if (atr < tick)
            atr = tick;

        var floor = MinimumThresholdTicks * tick;
        var candle = GetCandle(closed);

        _engine.Update(
            closed,
            (double)candle.High,
            (double)candle.Low,
            (double)Math.Max(atr * _majorThresholdAtr, floor),
            (double)Math.Max(atr * _minorThresholdAtr, floor),
            _minBarsBetweenPivots);

        var isLong = ResolveDirection(closed);
        var entry = candle.Close;

        decimal stop;

        if (_selector.TrySelectAnchor(
                _engine.Pivots, closed, isLong, (double)entry,
                (double)(_minDistanceAtr * atr), (double)(_maxDistanceAtr * atr),
                _lookbackPivots, _skipMostRecentPivot, out var anchor))
        {
            var level = (decimal)anchor;
            stop = isLong ? level - _bufferTicks * tick : level + _bufferTicks * tick;
            stop = RoundToTick(stop, tick);

            _structural[closed] = stop;
            _anchor[closed] = level;
            _structuralCount++;
        }
        else
        {
            // Repli. A structural level sitting inside the noise band is not a stop, it is a
            // donation — so when nothing clears the floor the volatility stop is the safer answer,
            // even though it is anchored to nothing.
            stop = isLong ? entry - _fallbackAtrMultiple * atr : entry + _fallbackAtrMultiple * atr;
            stop = RoundToTick(stop, tick);

            _fallback[closed] = stop;
            _fallbackCount++;
        }

        var distance = Math.Abs(entry - stop);
        _distanceTicks.Add((double)(distance / tick));
        _distanceAtr.Add((double)(distance / atr));
    }

    private bool ResolveDirection(int closed)
    {
        if (_previewDirection == TdStopPreviewDirection.Achat) return true;
        if (_previewDirection == TdStopPreviewDirection.Vente) return false;
        return GetCandle(closed).Close >= _ema[closed];
    }

    /// <summary>
    /// NinjaTrader had Instrument.MasterInstrument.RoundToTickSize. ATAS has no equivalent, but it
    /// does not need one: rounding in decimal is exact, where the NinjaTrader version worked in
    /// double and could land a hair off a tick boundary.
    /// </summary>
    private static decimal RoundToTick(decimal price, decimal tick)
    {
        return tick <= 0m ? price : Math.Round(price / tick) * tick;
    }

    #region Reglages

    [Parameter]
    [Display(Name = "Periode ATR", Description = "Unite de mesure commune au seuil de detection et au couloir.", GroupName = "Detection", Order = 1)]
    [Range(2, 10000)]
    public int AtrPeriod
    {
        get => _atr.Period;
        set { _atr.Period = value; RecalculateValues(); }
    }

    [Parameter]
    [Display(Name = "Seuil structurel (x ATR)", Description = "A garder identique a celui de TD Swing Structure.", GroupName = "Detection", Order = 2)]
    [Range(0.1, 1000)]
    public decimal MajorThresholdAtr
    {
        get => _majorThresholdAtr;
        set { _majorThresholdAtr = value; RecalculateValues(); }
    }

    [Parameter]
    [Display(Name = "Seuil intermediaire (x ATR)", Description = "A garder identique a celui de TD Swing Structure.", GroupName = "Detection", Order = 3)]
    [Range(0.1, 1000)]
    public decimal MinorThresholdAtr
    {
        get => _minorThresholdAtr;
        set { _minorThresholdAtr = value; RecalculateValues(); }
    }

    [Parameter]
    [Display(Name = "Barres minimum entre pivots", GroupName = "Detection", Order = 4)]
    [Range(1, 10000)]
    public int MinBarsBetweenPivots
    {
        get => _minBarsBetweenPivots;
        set { _minBarsBetweenPivots = value; RecalculateValues(); }
    }

    [Parameter]
    [Display(Name = "Distance minimale (x ATR)", Description = "Plancher du couloir. En dessous, le stop est dans le bruit d'une barre normale.", GroupName = "Couloir", Order = 10)]
    [Range(0.1, 1000)]
    public decimal MinDistanceAtr
    {
        get => _minDistanceAtr;
        set { _minDistanceAtr = value; RecalculateValues(); }
    }

    [Parameter]
    [Display(Name = "Distance maximale (x ATR)", Description = "Plafond du couloir. Au-dela, le niveau est structurel mais le risque ne l'est plus.", GroupName = "Couloir", Order = 11)]
    [Range(0.1, 1000)]
    public decimal MaxDistanceAtr
    {
        get => _maxDistanceAtr;
        set { _maxDistanceAtr = value; RecalculateValues(); }
    }

    [Parameter]
    [Display(Name = "Marge sous le niveau (ticks)", Description = "Le stop se pose au-dela du niveau retenu, pas dessus.", GroupName = "Couloir", Order = 12)]
    [Range(0, 10000)]
    public int BufferTicks
    {
        get => _bufferTicks;
        set { _bufferTicks = value; RecalculateValues(); }
    }

    [Parameter]
    [Display(Name = "Sauter le pivot le plus recent", Description = "Uniquement si un autre candidat valide existe. Le niveau le plus evident est celui que la chasse balaie.", GroupName = "Couloir", Order = 13)]
    public bool SkipMostRecentPivot
    {
        get => _skipMostRecentPivot;
        set { _skipMostRecentPivot = value; RecalculateValues(); }
    }

    [Parameter]
    [Display(Name = "Pivots examines", Description = "Profondeur de l'historique consulte, par cote.", GroupName = "Couloir", Order = 14)]
    [Range(2, 10000)]
    public int LookbackPivots
    {
        get => _lookbackPivots;
        set { _lookbackPivots = value; RecalculateValues(); }
    }

    [Parameter]
    [Display(Name = "Repli : distance ATR", Description = "Stop de volatilite utilise quand aucun niveau ne franchit le plancher.", GroupName = "Couloir", Order = 15)]
    [Range(0.1, 1000)]
    public decimal FallbackAtrMultiple
    {
        get => _fallbackAtrMultiple;
        set { _fallbackAtrMultiple = value; RecalculateValues(); }
    }

    [Parameter]
    [Display(Name = "Sens suppose", Description = "SuivreEma : achat au-dessus de l'EMA, vente en dessous.", GroupName = "Apercu", Order = 20)]
    public TdStopPreviewDirection PreviewDirection
    {
        get => _previewDirection;
        set { _previewDirection = value; RecalculateValues(); }
    }

    [Parameter]
    [Display(Name = "Periode EMA", GroupName = "Apercu", Order = 21)]
    [Range(1, 10000)]
    public int EmaPeriod
    {
        get => _ema.Period;
        set { _ema.Period = value; RecalculateValues(); }
    }

    #endregion
}
```

- [ ] **Étape 2 : compiler les deux projets**

```bash
dotnet build src/Atas.Indicators.TradeDeck/Atas.Indicators.TradeDeck.csproj -c Release
dotnet build src/NinjaTrader.Scripts/NinjaTrader.Scripts.csproj -c Release
```

Attendu : `Build succeeded` pour les deux.

- [ ] **Étape 3 : déployer et vérifier**

Redéployer, poser « TD Stop Placement » sur un graphique 200 ticks.

Attendu :
- une ligne orange sous le prix en tendance haussière, au-dessus en tendance baissière ;
- des segments gris quand aucun niveau ne convient (le repli ATR) ;
- des points dorés sur les niveaux retenus, **posés sur des pivots**, et non entre deux ;
- le stop est toujours **du bon côté** du prix.

Un stop du mauvais côté signifie que `isLong` et le signe du décalage se contredisent — relire
`ResolveDirection` et le calcul de `stop`.

- [ ] **Étape 4 : commit**

```bash
git add src/Atas.Indicators.TradeDeck/TdStopPlacement.cs
git commit -m "Porter TdStopPlacement sur ATAS X

Le couloir de distance vient de TdStopSelector, partage avec NinjaTrader : ce
fichier ne contient que l affichage, les reglages et le repli.

RoundToTickSize n a pas d equivalent chez ATAS, mais n en a pas besoin : l
arrondi en decimal est exact, la ou la version NinjaTrader travaillait en
double et pouvait tomber a un cheveu d une borne de tick."
```

---

## Tâche 6 : la notation du balayage

**Fichiers :**
- Modifier : `src/Atas.Indicators.TradeDeck/TdStopPlacement.cs`

- [ ] **Étape 1 : ajouter les champs et le type**

Dans `src/Atas.Indicators.TradeDeck/TdStopPlacement.cs`, ajouter aux `using` :

```csharp
using System.Globalization;

using OFT.Rendering.Context;
```

Ajouter aux champs :

```csharp
    private readonly List<PendingEntry> _pending = new();
    private int _resolved;
    private int _swept;
    private int _sweepHorizonBars = 20;
    private bool _showStatistics = true;
```

Ajouter le type imbriqué, avant la région `Reglages` :

```csharp
    /// <summary>A preview awaiting its verdict.</summary>
    private sealed class PendingEntry
    {
        public int Bar;
        public decimal Stop;
        public bool IsLong;
        public bool Hit;
    }
```

- [ ] **Étape 2 : enregistrer et résoudre les aperçus**

Dans `OnCalculate`, juste avant la fermeture de la méthode (après le calcul de `distance`), ajouter :

```csharp
        _pending.Add(new PendingEntry { Bar = closed, Stop = stop, IsLong = isLong });
        ResolvePending(closed, candle);
```

Ajouter la méthode, après `ResolveDirection` :

```csharp
    /// <summary>
    /// Scores past previews once enough bars have elapsed. Resolution is always backward-looking: a
    /// pending entry is only ever tested against bars that have already printed.
    /// </summary>
    private void ResolvePending(int closed, IndicatorCandle candle)
    {
        for (var i = _pending.Count - 1; i >= 0; i--)
        {
            var pending = _pending[i];

            // The entry is taken at this bar's close, so this bar cannot stop it out.
            if (pending.Bar == closed)
                continue;

            if (!pending.Hit)
            {
                var breached = pending.IsLong ? candle.Low <= pending.Stop : candle.High >= pending.Stop;
                if (breached)
                    pending.Hit = true;
            }

            if (closed - pending.Bar < _sweepHorizonBars)
                continue;

            _resolved++;
            if (pending.Hit)
                _swept++;

            _pending.RemoveAt(i);
        }
    }
```

Dans `OnRecalculate`, ajouter la remise à zéro :

```csharp
        _pending.Clear();
        _resolved = 0;
        _swept = 0;
```

- [ ] **Étape 3 : ajouter l'encart et ses deux réglages**

Ajouter la méthode de rendu, après `RoundToTick` :

```csharp
    protected override void OnRender(RenderContext context, DrawingLayouts layout)
    {
        if (!_showStatistics || ChartInfo == null || Container == null)
            return;

        var evaluated = _structuralCount + _fallbackCount;
        if (evaluated == 0)
            return;

        var lines = new List<string>
        {
            string.Format(CultureInfo.InvariantCulture,
                "{0} barres evaluees : {1:F1} % structurel, {2:F1} % repli ATR",
                evaluated,
                100.0 * _structuralCount / evaluated,
                100.0 * _fallbackCount / evaluated)
        };

        if (_distanceTicks.Count > 0)
        {
            lines.Add(string.Format(CultureInfo.InvariantCulture,
                "largeur du stop : moy. {0:F1} ticks ({1:F2} ATR), med. {2:F1} ticks",
                StatsBox.Mean(_distanceTicks), StatsBox.Mean(_distanceAtr), StatsBox.MedianOf(_distanceTicks)));
        }

        if (_resolved > 0)
        {
            lines.Add(string.Format(CultureInfo.InvariantCulture,
                "balayage a {0} barres : {1:F1} % ({2} touches sur {3})",
                _sweepHorizonBars, 100.0 * _swept / _resolved, _swept, _resolved));
        }

        StatsBox.Draw(context, Container, lines);
    }
```

Ajouter les deux réglages, à la fin de la région `Reglages` :

```csharp
    [Parameter]
    [Display(Name = "Horizon de balayage (barres)", Description = "Fenetre sur laquelle on mesure si le stop aurait ete touche.", GroupName = "Apercu", Order = 22)]
    [Range(1, 10000)]
    public int SweepHorizonBars
    {
        get => _sweepHorizonBars;
        set { _sweepHorizonBars = value; RecalculateValues(); }
    }

    [Parameter]
    [Display(Name = "Afficher les statistiques", Description = "Encart de calibration : part structurelle, largeur du stop, taux de balayage.", GroupName = "Apercu", Order = 23)]
    public bool ShowStatistics
    {
        get => _showStatistics;
        set { _showStatistics = value; RecalculateValues(); }
    }
```

- [ ] **Étape 4 : compiler les deux projets**

```bash
dotnet build src/Atas.Indicators.TradeDeck/Atas.Indicators.TradeDeck.csproj -c Release
dotnet build src/NinjaTrader.Scripts/NinjaTrader.Scripts.csproj -c Release
```

Attendu : `Build succeeded` pour les deux.
Si `CS0246: IndicatorCandle introuvable`, remplacer le type du paramètre de `ResolvePending` par
`var` en passant plutôt `closed` et en rappelant `GetCandle(closed)` à l'intérieur.

- [ ] **Étape 5 : déployer et vérifier**

Attendu : l'encart affiche trois lignes, et le **taux de balayage n'est pas 0 % ni 100 %** sur une
journée complète. 0 % signale que rien n'est jamais résolu — vérifier `SweepHorizonBars` et la garde
`pending.Bar == closed`.

- [ ] **Étape 6 : commit**

```bash
git add src/Atas.Indicators.TradeDeck/TdStopPlacement.cs
git commit -m "Noter le balayage et afficher les statistiques du stop

La resolution reste strictement retrospective : un apercu n est teste que
contre des barres deja imprimees. Sans ca l indicateur se noterait lui-meme sur
un futur qu il n aurait pas pu connaitre, et paraitrait excellent."
```

---

## Tâche 7 : la recette croisée et la documentation

C'est ici que le portage se prouve. Jusqu'à présent on a vérifié qu'ATAS affiche *quelque chose* ;
il reste à vérifier qu'il affiche **la même chose** que NinjaTrader.

**Fichiers :**
- Modifier : `docs/strategie-structure-marche.md`
- Modifier : `CLAUDE.md`

- [ ] **Étape 1 : mesurer les deux plateformes sur la même session**

Choisir **un** instrument, **une** unité de temps (200 ticks), **une** journée complète et rejouée
des deux côtés, avec les réglages par défaut, identiques.

Relever, pour `TdSwingStructure` :

| Mesure | NinjaTrader | ATAS X |
|---|---|---|
| Sommets majeurs | | |
| Creux majeurs | | |
| Pivots mineurs | | |
| Amplitude médiane (ticks) | | |
| Durée médiane (barres) | | |

Puis pour `TdStopPlacement` : part structurelle, largeur médiane du stop, taux de balayage.

Attendu : **les nombres concordent.** Le moteur ne peut pas être en cause — c'est le même fichier —
donc un écart désigne son entrée :

- un écart de **comptage de pivots** pointe le séquencement des barres. Vérifier que la garde
  `_lastBar` nourrit bien `bar - 1`, et que la fenêtre de chauffe `closed < _atr.Period + 1` est
  franchie au même endroit qu'en NinjaTrader (`CurrentBar < AtrPeriod + 1`) ;
- un écart d'**amplitude** pointe le seuil, donc l'ATR ou le `TickSize`. Les formules ATR sont
  identiques au caractère près entre les deux plateformes : chercher plutôt un `InstrumentInfo` nul
  au démarrage, ou un plancher de deux ticks appliqué à un `TickSize` différent ;
- un écart sur le **seul taux de balayage** pointe `ResolvePending`, pas la détection.

Un écart de quelques pivots aux extrémités de la session est acceptable — les deux plateformes ne
commencent pas forcément leur historique sur la même barre. Un écart au milieu ne l'est pas.

- [ ] **Étape 2 : vérifier que NinjaTrader compile toujours chez lui**

Le contrôle le plus important du plan, et le seul que `dotnet build` ne fait pas.

NinjaTrader **ouvert**, copier les sources :

```powershell
$nt = "$env:USERPROFILE\Documents\NinjaTrader 8\bin\Custom"
Copy-Item "src\NinjaTrader.Scripts\Indicators\*.cs" "$nt\Indicators\" -Force
```

Attendu : NinjaTrader recompile seul et recharge les indicateurs. Aucune erreur dans la fenêtre de
sortie NinjaScript. Les indicateurs personnels du trader fonctionnent toujours.

Si NinjaTrader était **fermé** pendant la copie, il ne verra jamais ces fichiers arriver : il faut
alors compiler à la main, *Control Center → New → NinjaScript Editor → F5*.

- [ ] **Étape 3 : documenter le portage**

Ajouter à la fin de `docs/strategie-structure-marche.md` :

```markdown
---

# Sur ATAS X

Les mêmes deux indicateurs existent sur ATAS X, dans `src/Atas.Indicators.TradeDeck/`. **Ils ne
dupliquent pas l'algorithme** : `TdSwingEngine.cs` et `TdStopSelector.cs` restent dans
`src/NinjaTrader.Scripts/Indicators/` et sont compilés par les deux projets, liés par
`<Compile Include>`. Un seul fichier sur le disque, deux compilateurs.

**Conséquence à connaître avant de toucher un fichier partagé.** Ils doivent rester en C# 9 /
`net48`, la contrainte de NinjaScript. Une construction plus moderne compile côté ATAS et casse la
compilation NinjaScript **entière** chez le trader — ses propres indicateurs compris. Le garde-fou
est `dotnet build src/NinjaTrader.Scripts` : les deux builds passent, ou aucun.

```bash
dotnet build src/NinjaTrader.Scripts/NinjaTrader.Scripts.csproj -c Release
dotnet build src/Atas.Indicators.TradeDeck/Atas.Indicators.TradeDeck.csproj -c Release
```

Déploiement : le DLL dans `%APPDATA%\ATAS X\Indicators\`, ou le bouton « ajouter un indicateur
personnalisé », qui charge sans redémarrer.

Trois différences de plateforme valent d'être connues :

- **La barre close se gère à la main.** ATAS rappelle `OnCalculate` à chaque tick de la barre en
  formation, là où `Calculate.OnBarClose` ne l'appelait qu'une fois par barre close. Le moteur est
  une machine à états : on n'avance que lorsque l'index bouge, et on lui donne `bar - 1`.
- **`MaximumBarsLookBack` n'existe plus comme problème.** L'indexation est absolue, l'écriture
  rétroactive d'un pivot est triviale.
- **`decimal` n'a pas de `NaN`.** `ShowZeroValue = false` remplace la remise à `double.NaN` par
  barre, et rend inutile la remise à zéro elle-même.

Les statistiques de calibration ne passent plus par `Print` — ATAS n'a pas de fenêtre de sortie —
mais par un encart dessiné sur le graphique. Elles servent aussi de recette : mêmes réglages, même
session, les nombres des deux plateformes doivent concorder.
```

- [ ] **Étape 4 : signaler la frontière dans `CLAUDE.md`**

Dans `CLAUDE.md`, ajouter à la section « Pièges à connaître » :

```markdown
- **`TdSwingEngine.cs` et `TdStopSelector.cs` sont compilés par TROIS chaînes** : NinjaScript chez le
  trader, `NinjaTrader.Scripts.csproj`, et le projet ATAS `src/Atas.Indicators.TradeDeck/` qui les
  lie par `<Compile Include>`. Ils doivent donc rester en **C# 9 / `net48`** — namespace à portée de
  fichier, `record` ou `required` compilent côté ATAS et cassent la compilation NinjaScript
  **entière** chez le trader, indicateurs personnels compris. Après toute modification de l'un
  d'eux : `dotnet build src/NinjaTrader.Scripts` **et** le build ATAS, les deux ou aucun.
```

- [ ] **Étape 5 : commit**

```bash
git add docs/strategie-structure-marche.md CLAUDE.md
git commit -m "Documenter le portage ATAS et la frontiere de source partagee

La contrainte a retenir n est pas ATAS, c est NinjaScript : les fichiers
partages restent en C# 9 / net48, et une construction plus moderne casse la
compilation NinjaScript entiere chez le trader. Le garde-fou est le build de
NinjaTrader.Scripts, a relancer apres chaque modification d un fichier
partage."
```

---

## Auto-relecture

**Couverture de la spécification.** Chaque décision de la spec est portée par une tâche : les deux
fichiers partagés liés par MSBuild (tâches 1 et 2), l'extraction du couloir (tâche 1), le namespace
neutre (tâche 1), le moteur laissé en `double` avec conversion aux bornes (tâches 3 et 5),
`TdStructureViewer` non porté (absent du plan, comme prévu), la barre close (tâche 3), l'ATR et
l'EMA natifs par `Add()` (tâches 3 et 5), `ShowZeroValue` en remplacement du `NaN` (tâches 3 et 5),
l'encart de statistiques (tâches 4 et 6), le `.csproj` `net10.0` sans WPF (tâche 2), le déploiement
dans `%APPDATA%\ATAS X\Indicators\` (tâche 2), et les cinq points de la recette (tâches 2, 3, 5
et 7).

**Points laissés ouverts, avec leur porte de sortie.** Trois appels d'API n'ont pas pu être vérifiés
autrement que par la documentation et les exemples officiels — `RedrawChart()` (tâche 4, étape 3),
le type `IndicatorCandle` du paramètre de `ResolvePending` (tâche 6, étape 4), et l'exactitude de
`Container.Region` pour le placement de l'encart. Chacun porte sa correction dans l'étape qui
l'emploie ; aucun n'est bloquant.

**Cohérence des types.** `TrySelectAnchor` a la même signature à sa définition (tâche 1) et à son
appel (tâche 5). `StatsBox.Mean` / `MedianOf` / `Draw` sont définis en tâche 4 et employés en
tâches 4 et 6. `TdSwingPivot` n'est lu que par ses champs existants.
