# Détection de structure de marché — étape 1

Premier module de la future catégorie « Stratégies » du deck. **Détection seule** : creux et
sommets structurels, affichés en points, sans aucune logique d'entrée, de stop, de target ni de
gestion de position. L'objectif de cette étape est de pouvoir juger *à l'œil* la qualité de la
détection sur des régimes différents avant de construire quoi que ce soit dessus.

Contexte d'ensemble : `.claude/plans/` (étude d'architecture de la catégorie « Stratégies »).

## Ce qui est livré

| Fichier | Rôle |
|---|---|
| `Indicators/TdSwingEngine.cs` | La détection elle-même, **classe simple** sans dépendance NinjaScript |
| `Indicators/TdSwingStructure.cs` | Affiche les pivots et l'EMA (étape 1) |
| `Indicators/TdStopPlacement.cs` | Choisit et prévisualise le stop (étape 2). **Ne passe aucun ordre** |
| `Strategies/TdStructureViewer.cs` | Stratégie mince qui affiche l'indicateur. **Ne passe aucun ordre** |
| `NinjaTrader.Scripts.csproj` | Projet de vérification de compilation |

Tous sous `src/NinjaTrader.Scripts/`.

Le moteur est une **classe ordinaire**, pas un indicateur, et chaque consommateur en possède sa
propre instance qu'il alimente avec ses propres barres. C'est ce qui évite qu'un indicateur en
héberge un autre — voir le piège d'ordre de compilation plus bas — et c'est la brique « filtre
autonome réutilisable » prévue par l'architecture du deck.

L'affichage vit dans un **indicateur** et non dans la stratégie : on peut le poser sur un graphique
et régler les paramètres en direct sans relancer un backtest.

## L'algorithme

### Pourquoi pas des fractales

Le détecteur évident — « un plus haut supérieur aux n barres de part et d'autre », ce que font les
fractales de Williams et le `Swing(n)` natif de NinjaTrader — est un test purement **positionnel**.
Il ne sait rien de la distance parcourue par le prix : en range il valide chaque micro-oscillation,
et en tendance forte il rate les pullbacks courts parce que `n` est fixe. La structure de marché
n'est pas une affaire de nombre de barres, c'est une affaire de **distance**.

### Machine à états alternée

On ne cherche pas tous les extrema pour les filtrer ensuite — on cherche alternativement un sommet,
puis un creux, puis un sommet. L'alternance est donc garantie par construction, sans
post-traitement, et le bruit est éliminé par l'amplitude et non par un comptage de barres.

```
état = CHERCHE_SOMMET, extrême = High[0], barreExtrême = barre courante

à chaque barre :
  si CHERCHE_SOMMET :
      si High[0] > extrême          → on prolonge : extrême = High[0]
      sinon si extrême - Low[0] >= seuil
                                    → VALIDER un sommet sur barreExtrême
                                       basculer en CHERCHE_CREUX
  symétrique pour CHERCHE_CREUX
```

Propriété utile : la barre de confirmation est toujours la **première** dont le retracement franchit
le seuil, donc son propre extrême opposé est nécessairement le point de départ de la nouvelle
jambe — toute barre antérieure allée plus loin aurait déclenché avant.

### Le seuil est un multiple d'ATR

Ni en ticks, ni en pourcentage. Un seuil en ticks se recalibre à chaque instrument et à chaque
changement de volatilité ; un pourcentage n'a pas de sens sur des futures. Exprimé en multiples
d'ATR, le même réglage tient en tendance et en range, à l'ouverture et à midi, sur MNQ comme
ailleurs : l'ATR absorbe lui-même le changement de régime.

Un plancher de 2 ticks est appliqué en dur : sur une série atone l'ATR s'effondre et chaque barre
validerait un pivot.

### Deux niveaux affichés en même temps

C'est le seul vrai conflit de l'algorithme. En range les vagues sont amples, un seuil élevé donne
une structure propre. En tendance forte les pullbacks sont **courts**, et ce sont justement ces
creux peu profonds qui comptent. Un seuil unique ne peut pas servir les deux :

| Niveau | Défaut | Rendu | Rôle |
|---|---|---|---|
| Structurel | `2.5 × ATR` | gros points, rouge / vert vifs | la structure, les poches de liquidité |
| Intermédiaire | `1.0 × ATR` | petits points, teintes atténuées | les pullbacks de tendance |

Les deux tournent en parallèle sur des machines indépendantes. On regarde, on tranche, puis on coupe
`ShowMinorPivots`.

### Extrêmes sur les mèches

`High` et `Low`, jamais `Close`. Une poche de liquidité, ce sont des stops posés *au-delà* du plus
haut ; travailler sur les clôtures déplacerait tous les niveaux de plusieurs ticks vers l'intérieur,
là où ils ne servent à rien.

### Ce qui a été écarté

Un filtre « la barre de l'extrême doit être un extremum local sur ±N barres » figurait dans la
proposition initiale. Il est retiré : la barre de l'extrême est fixe une fois choisie, donc un test
qui échoue sur elle échoue pour toujours et bloque la machine à états. Il faisait par ailleurs
double emploi avec le critère d'amplitude, qui est le seul filtre qui compte. Reste
`MinBarsBetweenPivots`, mesuré depuis le **pivot** précédent et non depuis la confirmation
précédente — c'est ce qui l'empêche de bloquer, la barre courante avançant toujours.

## Affichage

**Des plots, pas des `Draw.Dot`.** Sur tout un historique en 200 ticks, le dessin d'objets produit
des dizaines de milliers d'éléments graphiques et rend NinjaTrader inutilisable. Cinq plots
(`SwingHigh`, `SwingLow`, `MinorHigh`, `MinorLow`, `EMA`), à `NaN` partout sauf sur les barres de
pivot : rendu natif, rapide, sans limite de quantité.

Deux conséquences dans le code, toutes deux nécessaires :

- `MaximumBarsLookBack = MaximumBarsLookBack.Infinite` — un pivot est écrit **rétroactivement** dans
  la barre où l'extrême a eu lieu, qui peut être loin derrière la barre de confirmation pendant une
  longue jambe de tendance. Avec la fenêtre de 256 barres par défaut, ces écritures tombent hors de
  la série et sont perdues en silence ;
- chaque plot est remis à `NaN` en début de barre. Une `Series<double>` s'initialise à 0, et un
  point tracé à 0 écraserait l'échelle du graphique sur l'axe à chaque barre sans pivot.

## Limite à connaître

**Un pivot n'est confirmable qu'après coup**, par définition : on ne sait qu'un plus haut était un
sommet qu'une fois le prix redescendu de `k × ATR`. À l'écran c'est invisible et le résultat semble
parfait ; en direct, le point apparaît quelques barres après l'extrême qu'il marque. Ce n'est pas un
défaut à corriger, mais **rien de ce qui sera construit dessus ne pourra supposer qu'un pivot était
connu au moment où il s'est formé**.

## Paramètres

Tous en `[NinjaScriptProperty]`, ce qui les rendra découvrables et affichables par le deck sans
écrire une ligne côté interface.

| Paramètre | Défaut | Effet |
|---|---|---|
| `AtrPeriod` | 20 | Fenêtre de l'ATR. Longue = seuil stable |
| `MajorThresholdAtr` | 2.5 | Retracement minimal d'un pivot structurel |
| `MinorThresholdAtr` | 1.0 | Idem pour les pivots intermédiaires |
| `ShowMinorPivots` | `true` | Affiche le second niveau |
| `MinBarsBetweenPivots` | 3 | Anti-grappe |
| `DotOffsetTicks` | 4 | Écart entre le point et la mèche |
| `ShowEma` / `EmaPeriod` | `true` / 40 | Référence de tendance, exigée sur le graphique |

## Installer et lancer

NinjaScript compile les sources lui-même : on copie des `.cs`, **jamais un DLL**.

```powershell
$nt = "$env:USERPROFILE\Documents\NinjaTrader 8\bin\Custom"
Copy-Item "src\NinjaTrader.Scripts\Indicators\*.cs"                 "$nt\Indicators\" -Force
Copy-Item "src\NinjaTrader.Scripts\Strategies\TdStructureViewer.cs" "$nt\Strategies\" -Force
```

C'est du **PowerShell** : dans `cmd`, ni `$env:` ni `Copy-Item` n'existent, et la copie échoue en
silence.

Puis dans NinjaTrader : éditeur NinjaScript → **F5**. Poser `TdSwingStructure` ou `TdStopPlacement`
sur un graphique 200 ticks.

### Le piège : un indicateur neuf n'a pas encore de méthode générée

NinjaScript fabrique la méthode `TdSwingStructure(...)` à partir des indicateurs qu'il connaît
**déjà**. Un indicateur tout neuf n'en a donc pas avant la compilation *suivante* : tout code qui
l'appelle échoue en `CS1955`, et comme une compilation NinjaScript est tout-ou-rien, cet échec
empêche aussi l'indicateur lui-même d'entrer dans l'assembly. Blocage circulaire.

C'est la raison pour laquelle la détection est une **classe simple** (`TdSwingEngine`) : une classe
ordinaire n'a ni méthode générée, ni ordre de compilation. Les trois indicateurs se compilent en une
passe.

Seule `TdStructureViewer` reste concernée, puisqu'une stratégie doit appeler la méthode générée pour
faire un `AddChartIndicator`. Elle s'installe donc **après** une première compilation réussie des
indicateurs.

Un **backtest de `TdStructureViewer` rapporte zéro trade** : c'est le résultat attendu, la stratégie
ne passe aucun ordre. Ce qui se juge ici, ce sont les points.

### Vérifier la compilation avant de déployer

```bash
dotnet build src/NinjaTrader.Scripts/NinjaTrader.Scripts.csproj -c Release
```

À faire systématiquement : une erreur de compilation dans un de ces fichiers ne casse pas seulement
notre code, elle casse la compilation NinjaScript **entière** — donc aussi les indicateurs et
stratégies personnels du trader. Le DLL produit ne sert à rien.

La stratégie est exclue de ce projet par défaut : elle appelle `TdSwingStructure(...)`, la méthode
que NinjaScript *génère* depuis l'indicateur, qui n'existe donc pas avant le premier F5. Une fois
celui-ci fait :

```bash
dotnet build src/NinjaTrader.Scripts/NinjaTrader.Scripts.csproj -c Release -p:IncludeStrategies=true
```

## Calibrer

À la fermeture, l'indicateur imprime dans la fenêtre de sortie le nombre de pivots, l'amplitude
moyenne et médiane des vagues en ticks, et leur durée en barres. Régler `k` à l'œil sur trois
journées est lent ; avoir les chiffres à côté en fait une décision.

Méthode : trois graphiques 200 ticks — une journée de tendance marquée, une de tendance faible, une
de range — et on monte `MajorThresholdAtr` jusqu'à ce que les points parasites disparaissent sans
que les vrais retournements commencent à manquer. Les points intermédiaires servent de témoin :
s'ils marquent des retournements que le niveau structurel rate, le seuil structurel est trop haut.

---

# Étape 2 — placement du stop (`TdStopPlacement`)

Choisir automatiquement un niveau de stop structurellement cohérent, 4 ticks au-delà, sans se faire
sortir par le bruit ni prendre un risque démesuré.

## Un couloir, pas un classificateur de régime

La formulation naturelle est « détecter le régime, puis choisir gros ou petit point ». C'est le
mauvais découpage : un classificateur bascule précisément aux transitions, c'est-à-dire aux pires
moments, et le label n'est qu'un intermédiaire vers ce qui compte réellement — **la distance**.

On filtre donc les niveaux candidats par un couloir de distance, et le choix gros/petit se fait
seul :

- en tendance soutenue, les creux de pullback sont proches ; le plus proche qui reste au-dessus du
  plancher est souvent un pivot intermédiaire ;
- en range, les pivots intermédiaires sont collés au prix, tombent sous le plancher, sont éliminés,
  et le pivot structurel l'emporte.

Le régime n'est jamais nommé, le comportement voulu est obtenu, et il y a deux réglages au lieu de
six.

## Pourquoi sauter le pivot le plus récent

Pas seulement parce qu'il est proche. Parce que c'est **le niveau le plus évident du graphique** :
c'est là que tous les stops sont posés, donc là que la chasse va. Ces pivots sont des poches de
liquidité — c'est toute la raison de les détecter.

Coder « toujours l'avant-dernier » serait faux dans l'autre sens : on prendrait un stop inutilement
large chaque fois que le dernier creux est déjà profond. La règle est donc : **passer le niveau
évident uniquement si un autre candidat valide existe**.

## L'algorithme

1. Candidats = pivots confirmés du bon côté (creux pour un achat, sommets pour une vente), au-delà
   du prix d'entrée, dans les `LookbackPivots` derniers de ce côté. Les deux niveaux sont
   confondus : la hiérarchie gros/petit ne sert plus une fois le couloir posé.
2. Filtre : `MinDistanceAtr × ATR ≤ distance ≤ MaxDistanceAtr × ATR`.
3. Si au moins deux candidats survivent, écarter le plus récent.
4. Retenir le plus proche des restants. `Stop = niveau − BufferTicks` (miroir pour une vente).
5. Aucun survivant → **repli sur un stop de volatilité** `entrée − FallbackAtrMultiple × ATR`. Un
   niveau structurel situé dans la bande de bruit n'est pas un stop, c'est un don.

## Aucun regard vers le futur

C'est la contrainte de correction principale de cette étape. Un pivot n'est connaissable qu'une fois
le prix retracé de `k × ATR` : le point est **dessiné sur la barre de l'extrême**, donc dans le
passé. Un sélecteur qui lirait « le dernier creux » sur le graphique fini lirait un creux qui
n'était pas encore connu au moment de l'entrée — le backtest serait magnifique et le live décevant,
sans que rien ne le signale.

D'où `TdSwingPivot.ConfirmationBar`, distinct de `ExtremeBar`, et un historique **ordonné par
confirmation**. Le sélecteur ne voit jamais que ce qui existait à l'instant évalué.

## Vérifier sans passer d'ordre

L'indicateur calcule un stop pour une entrée hypothétique à la clôture de **chaque** barre et le
trace : on obtient un escalier qui doit épouser la structure, et les décrochages sautent aux yeux.

Trois plots : `StopStructurel` (orange), `StopRepliAtr` (gris — la fréquence du gris dit tout de la
calibration) et `NiveauRetenu` (point doré, le pivot choisi avant la marge).

Le sens supposé vient de `PreviewDirection` : `SuivreEma` prend un achat au-dessus de l'EMA et une
vente en dessous, ce qui correspond à la façon dont le trade serait réellement pris.

À la fermeture, la fenêtre de sortie donne les deux nombres qui *sont* les deux contraintes :

- la part de barres servies par un niveau structurel contre le repli ATR ;
- la **largeur moyenne** du stop, en ticks et en multiples d'ATR ;
- le **taux de balayage** : sur une entrée hypothétique à chaque barre, combien de fois le stop
  aurait été touché dans les `SweepHorizonBars` barres suivantes.

Méthode de calibration : descendre `MinDistanceAtr` tant que le taux de balayage ne monte pas, le
remonter dès qu'il monte. La largeur moyenne dit ce que ça coûte.

## Paramètres

| Paramètre | Défaut | Effet |
|---|---|---|
| `MinDistanceAtr` | 1.0 | Plancher du couloir |
| `MaxDistanceAtr` | 3.0 | Plafond du couloir |
| `BufferTicks` | 4 | Marge au-delà du niveau retenu |
| `SkipMostRecentPivot` | `true` | Écarte le niveau évident si un autre survit |
| `LookbackPivots` | 12 | Profondeur d'historique consultée, par côté |
| `FallbackAtrMultiple` | 2.0 | Largeur du stop de repli |
| `PreviewDirection` | `SuivreEma` | Sens supposé de l'aperçu |
| `SweepHorizonBars` | 20 | Fenêtre de mesure du balayage |

Les quatre paramètres de détection (`AtrPeriod`, seuils, `MinBarsBetweenPivots`) sont répétés ici et
**doivent rester identiques** à ceux de `TdSwingStructure` : chaque indicateur possède sa propre
instance du moteur, et deux réglages divergents afficheraient des points qui ne sont pas ceux sur
lesquels le stop s'appuie.

## Pas encore fait

Le cas « le plus proche candidat dépasse le plafond » retombe aujourd'hui sur le stop ATR. Les deux
autres politiques envisagées — ramener au plafond, ou refuser le trade — n'auront de sens qu'avec
des entrées.

---

## Étape suivante

Rien n'est décidé tant que le placement du stop n'est pas jugé bon. Ensuite seulement : conditions
d'entrée, take profit, puis gestion de position (template ATM ou `SetStopLoss`/`SetProfitTarget`),
puis exposition de la stratégie au deck.
