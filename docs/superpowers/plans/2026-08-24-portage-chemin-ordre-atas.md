# Portage du chemin d'ordre vers ATAS classique — plan d'implémentation

> **Pour un agent :** SOUS-COMPÉTENCE REQUISE — utiliser `superpowers:subagent-driven-development`
> (recommandé) ou `superpowers:executing-plans` pour exécuter ce plan tâche par tâche. Les étapes
> utilisent la syntaxe à cases (`- [ ]`).

**Objectif :** livrer une `ChartStrategy` ATAS **classique** qui parle le protocole du bridge à la
place de l'add-on NinjaTrader — les 13 commandes, la publication d'état, les refus, les rejets et le
journal.

> **La cible est ATAS classique, pas ATAS X.** Le support ATAS l'a confirmé par écrit : les Chart
> Strategies n'existent pas sur ATAS X (Beta). Tout ce plan vise donc `C:\Program Files (x86)\ATAS
> Platform` et `%APPDATA%\ATAS\Strategies`.

**Architecture :** le bridge et le deck-host ne connaissent que `docs/protocol.md` ; ils ne savent
pas qui est au bout du fil. Un client WebSocket ATAS qui parle ce protocole se branche à leur place
sans qu'ils s'en aperçoivent. **C'est la propriété qui rend ce portage possible, et aucune tâche de
ce plan ne modifie une ligne du bridge ou du deck-host.**

`ChartStrategy.Connector` est **toujours nul** sur le classique — mesuré sur 472 trames publiées et
confirmé par le support ATAS (« the connector must be assigned manually if you need it »). La
sélection du deck se résout donc contre le **registre de connecteurs de l'hôte**
(`OFT.Platform.Core`, atteint par réflexion via `CommonServiceLocator`), agrégée sur
`IConnectorsManager.Connectors`, et **posée explicitement** sur chaque ordre. Quand ce registre est
introuvable, la stratégie **dégrade** vers `TradingManager` — le compte et l'instrument du graphique
seuls — le dit une fois dans le journal, et continue de trader : ce mode dégradé fait partie de la
livraison, pas un repli oublié. La stratégie doit dans tous les cas être attachée à un graphique et
démarrée, avec un portefeuille sélectionné dans ses paramètres et un flux connecté.

**Pile technique :** C# récent / `net10.0`, DLL référencées depuis `C:\Program Files (x86)\ATAS
Platform`, `Newtonsoft.Json` pris dans ce même dossier. Un seul fichier reste partagé avec
NinjaTrader — `JournalSeal.cs` — et reste donc en C# 9 / `net48`. L'accès au registre de connecteurs
de l'hôte se fait **entièrement par réflexion**, sans aucune référence de compilation à
`OFT.Platform.Core`, `OFT.Platform` ni `CommonServiceLocator` (voir Contraintes globales).

**Spécification :** `docs/superpowers/specs/2026-08-21-portage-chemin-ordre-atas-design.md`

**Hors périmètre, explicitement :** la macro Tendance (`TrendEngine`, `TrendMonitor`) et le copieur
multi-comptes (`CopyEngine`) — spécification ultérieure. Le portage des indicateurs
(`2026-08-21-portage-indicateurs-atas.md`) n'a aucun lien avec celui-ci.

---

## Contraintes globales

Elles s'appliquent à **toutes** les tâches, implicitement.

- **Le bridge et le deck-host ne sont pas touchés.** Aucune tâche ne modifie `src/StreamDeckBridge/`
  ni `src/deck-host/`. Si une tâche semble l'exiger, c'est le signe qu'on a mal lu
  `docs/protocol.md` : **s'arrêter et demander**, plutôt que d'ouvrir une brèche dans la seule
  propriété qui rend ce portage possible.
- **`JournalSeal.cs` reste en C# 9 / `net48`.** Il est compilé par NinjaScript chez le trader : pas
  de namespace à portée de fichier, pas de `record`, pas de `required`, pas d'expression `switch`
  récente. Une construction trop moderne compile côté ATAS et **casse la compilation NinjaScript
  entière chez le trader** — ses indicateurs personnels compris, une compilation NinjaScript étant
  tout-ou-rien. Le reste du projet ATAS est libre (`LangVersion latest`).
- **L'accès au registre de connecteurs de l'hôte se fait entièrement par réflexion — aucune
  référence de compilation** à `OFT.Platform.Core`, `OFT.Platform` ni `CommonServiceLocator`. Ce
  sont des assemblies de l'**hôte**, pas l'API plugin documentée : leurs types et méthodes sont
  publics, mais ATAS ne garantit rien et ne préviendra pas s'ils bougent. Une référence dure et la
  stratégie ne se charge plus du tout le jour où l'un d'eux est renommé — cockpit mort, sans
  explication. Par réflexion, elle **dégrade** vers `TradingManager` (compte et instrument du
  graphique seuls), le dit une fois dans le journal, et continue de trader. Toute tâche qui touche
  `ContextResolver` ou la résolution compte/instrument est concernée, et le mode dégradé doit être
  écrit et testé comme le reste — ce n'est pas du code mort.
- **Les trois builds passent, jamais un seul :**
  ```bash
  dotnet build src/NinjaTrader.AddOn.StreamDeck/NinjaTrader.AddOn.StreamDeck.csproj -c Release
  dotnet build src/NinjaTrader.Scripts/NinjaTrader.Scripts.csproj -c Release
  dotnet build src/Atas.Strategy.TradeDeck/Atas.Strategy.TradeDeck.csproj -c Release
  ```
- **~180 avertissements `CS0436` côté NinjaTrader sont normaux** (types déjà présents dans
  `NinjaTrader.Custom.dll`). Ne pas chercher à les corriger.
- **Les codes d'erreur du protocole sont reconduits au caractère près** : `NO_MARKET_DATA`,
  `NO_POSITION`, `NO_STOP_ORDER`, `NO_TARGET_ORDER`, `INVALID_QUANTITY`, `INVALID_PAYLOAD`,
  `INVALID_STOP_PRICE`, `ACCOUNT_NOT_FOUND`, `INSTRUMENT_NOT_FOUND`, `CONTEXT_MISSING`,
  `ORDER_REJECTED`, `UNSUPPORTED_ACTION`, `INTERNAL_ERROR`. L'hôte les affiche sur la touche et le
  journal s'en sert : un code inventé n'apparaît nulle part.
- **`TRACE` obligatoire dans toute boucle périodique** — publication d'état toutes les 500 ms,
  trames `stateUpdate`. Un `INFO` à cet endroit produit des centaines de milliers de lignes par jour.
- **Rien ne bloque dans un gestionnaire d'événement du connecteur.** Ces événements n'arrivent pas
  sur le fil du graphique : tout état partagé s'écrit sous verrou, tout envoi est en
  *fire-and-forget*, aucune attente synchrone. C'est la même contrainte qui a produit `_sendLock`
  chez NinjaTrader, sous une autre forme.
- **Les commentaires expliquent le *pourquoi*, en anglais**, comme le reste du code. Ceux qui
  existent devant un garde-fou ne se suppriment pas : la raison est presque toujours un incident
  réel de trading.
- **Aucun test automatisé n'existe dans ce dépôt et ce plan n'en introduit pas.** La vérification de
  chaque tâche est une compilation **plus** un contrôle explicite, décrit pas à pas. Les tâches 4 et
  suivantes exigent ATAS classique ouvert, le bridge lancé sur ports isolés, et un compte de
  simulation.
- **Toujours sur ports isolés**, jamais 8218/8219 pendant qu'une installation tourne :
  ```powershell
  $env:SDBRIDGE_PluginPort = "9318"; $env:SDBRIDGE_AddonPort = "9319"
  ```
  Et **ne jamais sonder 8218** (`Test-NetConnection`, `curl`, `websocat`…) : la connexion reste
  ouverte et la commande se bloque.
- **Un seul add-on à la fois.** Le bridge n'accepte qu'un add-on : pendant tout essai ATAS,
  NinjaTrader doit être fermé. Deux clients se disputeraient la place et le diagnostic serait
  illisible.
- **`binaries/Atas.Strategy.TradeDeck/` apparaîtra dans `git status`** à chaque build Release : c'est
  la cible partagée `src/Directory.Build.targets` qui archive l'assembly du projet (nommé d'après le
  projet, `TradeDeck.Strategy.dll` en est la sortie). C'est attendu, il faut le committer, et
  `binaries/` ne doit jamais entrer dans `.gitignore`.
- **Un commit par tâche au minimum**, message en français, corps expliquant le pourquoi.

## Ce que ce portage laisse volontairement dégradé

À nommer maintenant plutôt qu'à découvrir en séance. Ces points sont **corrects par conception** et
ne doivent pas être « réparés » en cours de route :

| Ce que le trader verra | Pourquoi c'est correct |
|---|---|
| Registre introuvable : un seul compte, un seul instrument, ceux du graphique | `ChartStrategy.Connector` est toujours nul et le registre de connecteurs de l'hôte (`OFT.Platform.Core`) s'atteint par réflexion, sans référence de compilation — voir Contraintes globales. S'il devient introuvable (ATAS a renommé un type), la stratégie **dégrade** vers `TradingManager`, chart-scoped, le dit une fois dans le journal, et continue de trader plutôt que de refuser de se charger. C'est un mode livré et testé, pas un repli oublié. |
| Touche Tendance : `NO DATA` | Le protocole prévoit le bloc `trend` absent tant que le monitor n'est pas initialisé, et le bridge conserve alors la dernière valeur reçue. La macro Tendance est hors périmètre. |
| Touche Compte : suiveurs non résolus | Le bloc `copier` de l'add-on n'est pas publié ; le bridge réestampille sa moitié configuration et les champs venus de l'add-on restent à leur défaut. Le copieur est hors périmètre. |
| `setCopierConfig` / `configureTrend` refusés | Le bridge les **force** à chaque connexion d'add-on. La stratégie répond `UNSUPPORTED_ACTION` et le journalise une fois : mentir en répondant « succès » afficherait une copie active alors que rien ne recopie. |

Une omission de plus, elle assumée et signalée ici parce qu'elle **change** le comportement du
deck : l'add-on NinjaTrader élague les comptes « roulés » d'une société de prop
(`APEX-346280-35`, `-36` → seul le plus haut suffixe est listé, voir
`ContextResolver.PruneRolledAccountNames`). Ce plan ne porte pas cette heuristique : elle est
spécifique à la façon dont NinjaTrader énumère ces comptes, et l'agrégation sur
`IConnectorsManager.Connectors` ne présente pas forcément la même forme. Tous les portefeuilles
connectés sont listés. **Si le courtier ATAS du trader expose lui aussi des comptes roulés, c'est un
ajout à demander — pas à improviser.**

## Structure des fichiers

| Fichier | Responsabilité |
|---|---|
| `src/NinjaTrader.AddOn.StreamDeck/Utilities/JournalSeal.cs` | **partagé** — scellement HMAC chaîné. Namespace neutralisé, dépendances retirées (tâche 1). Reste physiquement là : l'installateur ramasse `src/NinjaTrader.AddOn.StreamDeck/**/*.cs`, le déplacer casserait la livraison chez le trader. |
| `src/NinjaTrader.AddOn.StreamDeck/Services/ExecutionRecorder.cs` | add-on NT — un `using` et une lambda de journalisation (tâche 1) |
| `src/Atas.Strategy.TradeDeck/Atas.Strategy.TradeDeck.csproj` | projet ATAS, références et lien partagé |
| `src/Atas.Strategy.TradeDeck/SdLog.cs` | journal de bord — `addon-AAAA-MM-JJ.log`, même format qu'aujourd'hui |
| `src/Atas.Strategy.TradeDeck/BridgeMessage.cs` | enveloppe du protocole, sur `Newtonsoft.Json` |
| `src/Atas.Strategy.TradeDeck/BridgeClient.cs` | `ws://127.0.0.1:8219`, reconnexion autonome, **verrou d'envoi** |
| `src/Atas.Strategy.TradeDeck/TradeDeckStrategy.cs` | `ChartStrategy` — cycle de vie, abonnements, routage des commandes non marchandes |
| `src/Atas.Strategy.TradeDeck/ContextResolver.cs` | sélection du deck → `Portfolio` + `Security` + `Position` + ordres, agrégés par réflexion sur `IConnectorsManager.Connectors` ; dégrade vers `TradingManager` si le registre est introuvable |
| `src/Atas.Strategy.TradeDeck/StatePublisher.cs` | publication toutes les 500 ms |
| `src/Atas.Strategy.TradeDeck/CommandDispatcher.cs` | aiguillage des 13 commandes |
| `src/Atas.Strategy.TradeDeck/OrderEngine.cs` | construit et poste les `Order` |
| `src/Atas.Strategy.TradeDeck/OrderMonitor.cs` | rejets et transitions d'état |
| `src/Atas.Strategy.TradeDeck/GuardEnforcer.cs` | annulation des ordres extérieurs |
| `src/Atas.Strategy.TradeDeck/ExecutionRecorder.cs` | le spool du journal |

## Écarts constatés par rapport à la spécification

Établis **par réflexion sur les assemblies réelles** et **par essai terrain sur ATAS classique**
(24/08/2026, journal `addon-2026-08-24.log`), confirmés à trois reprises par le support ATAS —
pas par lecture de la documentation. Chacun est repris dans la tâche concernée.

1. **`Portfolio.ConnectionState` n'est PAS dans `StockSharp.Messages`, contrairement à ce que cette
   note affirmait initialement.** `Portfolio.ConnectionState` est bien un `Nullable<ConnectionStates>`,
   mais ce `ConnectionStates`-là vit dans `Utils.Common.Network.Client` — déjà référencé par le
   projet pour une autre raison, donc aucune septième référence n'est nécessaire.
   `StockSharp.Messages` déclare bien un type portant le même nom court, mais c'est un type sans
   rapport : s'y référencer aurait fait échouer l'opérateur `==` avec un `CS0019` trompeur plutôt
   que de résoudre le vrai type. Corrigé à la tâche 3 après compilation en échec ; voir le
   commentaire dans `Atas.Strategy.TradeDeck.csproj`.
2. **`Newtonsoft.Json` se référence par `HintPath`, pas par NuGet.** ATAS livre
   `Newtonsoft.Json.dll` en 13.0.3 — exactement la version que le projet add-on utilise déjà. La
   référencer là où ATAS la charge évite deux Newtonsoft dans le même `AppDomain`, et suit ce que le
   projet indicateurs fait déjà pour `System.Drawing.Common`. Repli documenté si la version livrée
   devenait trop ancienne : `<PackageReference Include="Newtonsoft.Json" Version="13.0.3"
   ExcludeAssets="runtime" />`.
3. **`ATAS.DataFeedsCore.Order` n'a pas de `Name`.** Le champ qui porte la marque `"StreamDeck"`
   chez NinjaTrader s'appelle **`Comment`** ici. Toute la logique qui distingue « ordre du deck » de
   « ordre tapé à la main » repose dessus — et `GuardEnforcer` la double d'un suivi par référence,
   parce qu'un commentaire est une chaîne libre qu'un trader peut taper dans un ticket de DOM.
   Confirmé par l'essai terrain : un ordre `ChartTrader` manuel apparaît avec `Comment` vide.
4. **`OrderStates` ne compte que quatre valeurs brutes** — `None`, `Active`, `Done`, `Failed` — là
   où NinjaTrader en a une douzaine. Mais l'extension **`Order.Status()`**
   (`ATAS.DataFeedsCore.Extensions`) rend le verdict utilisable :
   `OrderStatus { None, Placed, Filled, PartlyFilled, Canceled }`. C'est elle qu'on emploie partout
   plutôt que de jongler avec `State`, `Canceled` et `Unfilled` — la documentation officielle de
   gestion des ordres ne présente d'ailleurs qu'elle.
5. **`ModifyOrder` prend un ordre NEUF, pas l'ordre muté.** Chez NinjaTrader on écrit
   `order.StopPriceChanged = x; account.Change(order)`. Ici c'est `ModifyOrder(ancien, clone_modifié)`
   — « cloner l'ordre existant, modifier prix ou volume, passer les deux références », dit la doc.
6. **Les ordres passent par `ChartStrategy.OpenOrder(order)`, pas par `ITradingManager`.** La spec
   citait `TradingManager.OpenOrder(order, setDefaultQuantity: false, askConfirmation: false,
   checkOrderStates: true)`. Cette surcharge existe, mais **ce n'est pas l'API documentée d'une
   stratégie** : la doc et l'exemple officiel `SmaChartStrategy` emploient `OpenOrder(order)`,
   `CancelOrder(order)`, `ModifyOrder(ancien, neuf)` — sans aucun booléen, **vérifié présent sur le
   classique par réflexion**. Cela **retire un des trois « vrais risques » de la spec** : il n'y a
   pas de `askConfirmation` à établir empiriquement sur ce chemin, puisqu'il n'y a pas de paramètre.
7. **`ChartStrategy.Portfolio` et `.Security` ont un accesseur en écriture qui LÈVE**
   (`NotSupportedException`) : le graphique les fixe, on ne peut pas repointer la stratégie. Et
   **`ChartStrategy.Connector` est toujours nul** — mesuré sur 472 trames publiées, confirmé par le
   support ATAS (« the connector must be assigned manually if you need it »). La conception de la
   spec — résoudre `Portfolio` et `Security` du deck contre le **registre de connecteurs de l'hôte**
   (agrégé sur `IConnectorsManager.Connectors`, jamais `ChartStrategy.Connector`) puis les poser
   explicitement sur chaque `Order` — n'est donc pas une élégance, c'est **le seul chemin possible** :
   ni le graphique ni son `Connector` ne peuvent être repointés. En mode dégradé (registre
   introuvable), ce chemin se réduit au couple `Portfolio` / `Security` du graphique — un seul
   compte, un seul instrument.
8. **`ChartStrategy.CurrentPosition` et `.AveragePrice` sont INTERNES à la stratégie**, la doc le
   dit explicitement — « distinct des positions au niveau du compte ». Le deck a besoin de la
   position du **compte** : elle vient de l'agrégation des `Positions` de chaque connecteur du
   registre de l'hôte (jamais de `ChartStrategy.Connector.Positions`, toujours nul), avec repli sur
   `TradingManager.Position` — chart-scoped — en mode dégradé.
9. **ATAS a un mode Replay qui produit de vrais objets `MyTrade`.**
   `ATAS.DataFeedsCore.Extensions.IsReplay()` existe sur `Portfolio`, `Order`, `Position` et
   `MyTrade`, et l'indicateur officiel `TradesOnChart` s'en sert pour choisir sa source de
   statistiques. **Sans garde, un rejeu historique enverrait des trades fabriqués à Bitlearn comme
   s'ils étaient réels.** L'add-on NinjaTrader s'en protégeait en excluant les comptes `Backtest` et
   `Playback101` ; l'équivalent ATAS est un fait, pas un nom.
10. **`TradingManager` est une propriété directe de `ATAS.Indicators.Indicator`**, dont
    `ChartStrategy` hérite — et non `DataProvider.TradingManager`, forme détournée que ce plan
    employait d'abord. Son accesseur est **`protected`** : seule la classe stratégie peut le lire
    directement, il faut le transmettre explicitement aux classes d'aide (`ContextResolver`,
    `StatePublisher`, …) qui n'héritent pas d'`Indicator`. Ce n'est plus un détail d'implémentation :
    `TradingManager` est devenu la **source du mode dégradé** quand le registre de connecteurs de
    l'hôte est introuvable — chart-scoped, un compte, un instrument. Confirmé par l'essai terrain :
    l'ordre `ChartTrader` manuel, sur le même couple compte + instrument que le graphique, y était
    visible ; un ordre passé sur un autre compte, non.
11. **`ChartStrategy.Connector` est toujours nul, systématiquement.** Mesuré sur 472 trames
    publiées, sur plusieurs sessions ; jamais de transition « connecteur sain » observée. Ne pas le
    retenter en repli : ce n'est pas un cas rare, c'est le comportement normal du classique.
12. **`ChartStrategy.Orders` vaut toujours 0**, même quand un ordre existe — vérifié en en passant
    un et en le laissant vivre pendant que la sonde tournait. La source des ordres est le registre
    de connecteurs agrégé (ou `TradingManager.Orders` en mode dégradé), jamais cette propriété.
13. **Le connecteur s'obtient par le registre de l'hôte, atteint par réflexion** :
    `CommonServiceLocator.ServiceLocator.Current.GetInstance(typeof(OFT.Platform.Core.Providers
    .ITradingCore)).GetConnector(portfolio)`, et `GetInstance(typeof(IConnectorsManager)).Connectors`
    pour la liste complète. Mesuré : `GetConnector(DEMO29ADC)` rend un `SbeConnector` exposant
    Portfolios=1, Securities=5, Orders=2, Positions=2, MyTrades=1 — deux ordres et deux positions là
    où `TradingManager` n'en montrait aucun au même instant ; `IConnectorsManager.Connectors` liste
    12 connecteurs, dont un Rithmic portant deux portefeuilles absents du graphique.
14. **`CoreProvider.TradingCore` est nul** — voie morte constatée en sonde. Résoudre `ITradingCore`
    directement via `ServiceLocator.Current.GetInstance`, jamais via `CoreProvider`.
15. **Les portefeuilles sont répartis sur plusieurs connecteurs, jamais un seul.**
    `ContextResolver` doit agréger sur `IConnectorsManager.Connectors`, en ignorant ceux qui ne
    portent aucun portefeuille — 10 des 12 connecteurs mesurés étaient dans ce cas.

## Surface d'API ATAS utilisée par ce plan

Relevé par réflexion, à ne pas redécouvrir tâche par tâche.

```
ChartStrategy (ATAS.Strategies.Chart)          : Portfolio, Security (SETTERS THROW),
                                                 OpenOrder(Order), CancelOrder(Order),
                                                 ModifyOrder(Order old, Order new),
                                                 OnStarted(), OnSuspended(), OnStopping(), OnStopped(),
                                                 StopWithNotification(string) /
                                                 StopWithNotificationAsync(string)
                                                 CurrentPosition, AveragePrice — STRATEGY-INTERNAL,
                                                 never the account position: do not use them
                                                 Connector — ALWAYS NULL on classic (measured across
                                                 472 published frames; confirmed in writing by ATAS
                                                 support). NEVER use it, and never fall back to it.
                                                 Orders — ALWAYS 0, even with a live order (measured).
                                                 NEVER use it; the source is the aggregated connector
                                                 registry below, or TradingManager.Orders degraded.
Indicator (base)                               : TradingManager → ITradingManager. Accessor is
                                                 PROTECTED: only the strategy class reaches it
                                                 directly, pass it down explicitly to helper classes.
                                                 THE DEGRADED-MODE SOURCE when the host registry
                                                 below is unreachable — chart-scoped, one account,
                                                 one instrument, same references as the chart's own
                                                 Portfolio/Security (measured).
                                                 Also: InstrumentInfo, ChartInfo, TradingStatisticsProvider
ITradingManager                                : IsStopLossOrder(Order), IsTakeProfitOrder(Order)
                                                 (its four-boolean OpenOrder/CancelOrder/ModifyOrder
                                                  overloads exist but are NOT the strategy API)
Extensions (ATAS.DataFeedsCore)                : Order.Status() → OrderStatus
                                                 { None, Placed, Filled, PartlyFilled, Canceled }
                                                 IsReplay() on Portfolio, Order, Position, MyTrade

--- Host registry — REFLECTION ONLY, no compile-time reference to any of these three (see
    Contraintes globales); this is the primary path, TradingManager above is the degraded fallback ---
CommonServiceLocator.ServiceLocator (static)   : IsLocationProviderSet, Current.GetInstance(Type)
OFT.Platform.Core.Providers.ITradingCore       : GetConnector(Portfolio) / GetConnector(Security)
                                                 → IDataFeedConnector
OFT.Platform.Core.Managers.Connections.IConnectorsManager
                                                : Connectors → IEnumerable<IDataFeedConnector>, every
                                                 connector in the platform. AGGREGATE over it, skip
                                                 connectors carrying no portfolio (measured: 10 of 12).
OFT.Platform.Core.Providers.CoreProvider       : TradingCore — ALWAYS NULL (measured). Do not use it;
                                                 resolve ITradingCore directly via ServiceLocator instead.
-----------------------------------------------------------------------------------------------------

IDataFeedConnector (ATAS.DataFeedsCore)        : Portfolios, Securities, Orders, Positions, MyTrades
                                                 GetPosition(Portfolio, Security, TPlusLimits?)
                                                 NewOrders, OrderChanged, OrdersRegisterFailed,
                                                 OrdersCancelFailed, OrderModifyFailed,
                                                 NewMyTrades, NewPositions, PositionsChanged,
                                                 NewPortfolios, PortfoliosChanged, ConnectionStateChanged
Order                                          : Portfolio, Security, Direction, Type, QuantityToFill,
                                                 Price, TriggerPrice, TimeInForce, OCOGroup, Comment,
                                                 Id, State, Canceled, Unfilled, Clone()
Portfolio                                      : AccountID, ClosedPnL, ClosedPnlDate, OpenPnL,
                                                 Balance, BalanceAvailable, IsRealAccount,
                                                 ConnectionState, TotalClosedPnL (À NE PAS UTILISER)
Security                                       : Code, Instrument, TickSize, TickCost,
                                                 BestBidPrice, BestAskPrice, LastTradePrice, Expiration
Position                                       : Portfolio, Security, Volume, AveragePrice,
                                                 UnrealizedPnL, RealizedPnL, IsInPosition
MyTrade                                        : Id, OrderId, Order, AccountID, Portfolio, Security,
                                                 OrderDirection, Price, Volume, Commission, Time
```

**Forme des délégués d'événement du connecteur** — le connecteur est **toujours** le premier
paramètre, ce que les noms des types ne laissent pas deviner :

```csharp
ConnectorEventHandler<T>        → (IDataFeedConnector c, T arg)
ConnectorEventHandler<T1,T2>    → (IDataFeedConnector c, T1 a1, T2 a2)
ConnectorEventHandler<T1,T2,T3> → (IDataFeedConnector c, T1 a1, T2 a2, T3 a3)

// D'où, concrètement :
NewOrders            : (c, IEnumerable<Order> orders)
OrderChanged         : (c, Order order)
OrdersRegisterFailed : (c, string message, Order order)
OrdersCancelFailed   : (c, string message, Order order)
OrderModifyFailed    : (c, Order order, Order newOrder, string error)
NewMyTrades          : (c, IEnumerable<MyTrade> trades)
NewPositions         : (c, IEnumerable<Position> positions)
PositionsChanged     : (c, IEnumerable<Position> positions)
ConnectionStateChanged : (c, ConnectionStateEventArgs args)
```

---

## Tâche 1 : rendre `JournalSeal` partageable

Tâche **entièrement NinjaTrader**, sans une ligne d'ATAS. Remaniement à comportement constant : à la
fin, l'add-on doit produire exactement les mêmes signatures qu'avant.

`JournalSeal.cs` dépend aujourd'hui de `SimpleJson` et de `SdLogger`, deux classes du namespace
NinjaTrader. Le lier par MSBuild depuis le projet ATAS en l'état ne compilerait pas. On retire les
deux dépendances plutôt que de les traîner : le fichier devient **sans aucune dépendance**, ce qui
est de toute façon la règle des fichiers que NinjaScript compile tels quels.

**La forme canonique — `Sep`, `ExecFields`, `Canonical`, `Text` — ne bouge pas d'un caractère.**
Elle doit rester identique à `deck-host/src/seal.ts` et à `Bitlearn/lib/tradeDeck/integrity.js`,
faute de quoi « rien ne scelle plus, et tous les journaux tombent silencieusement au niveau non
vérifiable ».

⚠ **Ce fichier est livré chez le trader.** L'installateur le dépose dans `AddOns\StreamDeck\`.
Le changement de namespace touche donc du code déployé et **impose une version et un installateur
reconstruit** (`docs/publier-une-version.md`) : sans ça le trader se retrouve avec un
`ExecutionRecorder` qui référence un namespace absent et une compilation NinjaScript en échec, tout
ou rien. Le rappel est repris à l'étape 8 et à la tâche 10.

**Fichiers :**
- Modifier : `src/NinjaTrader.AddOn.StreamDeck/Utilities/JournalSeal.cs`
- Modifier : `src/NinjaTrader.AddOn.StreamDeck/Services/ExecutionRecorder.cs:65` et `:100-107`

**Interfaces :**
- Produit : `TradeDeck.Journal.JournalSeal`, avec
  `JournalSeal(string directory, Action<JournalSealLevel, string, Exception> report = null)`,
  `bool Active { get; }` et `void Stamp(Dictionary<string, object> record)` — signatures inchangées
  hormis le second paramètre du constructeur.
- Produit : `enum TradeDeck.Journal.JournalSealLevel { Info, Warn, Error }`.

- [x] **Étape 1 : changer le namespace**

Dans `src/NinjaTrader.AddOn.StreamDeck/Utilities/JournalSeal.cs`, remplacer

```csharp
namespace NinjaTrader.NinjaScript.AddOns.StreamDeck.Utilities
{
```

par

```csharp
// Deliberately NOT a NinjaTrader.* namespace. This file is compiled by BOTH NinjaScript and the
// ATAS project (see src/Atas.Strategy.TradeDeck), and a NinjaTrader.* namespace inside an ATAS
// assembly makes the next reader doubt what they are looking at. NinjaScript compiles every .cs
// under bin\Custom into one assembly and places no constraint on the namespace of a plain class.
//
// C# 9 / net48 ONLY, like every shared file: a file-scoped namespace, a record or a required member
// compiles on the ATAS side and breaks the trader's ENTIRE NinjaScript compilation, his own
// indicators included.
namespace TradeDeck.Journal
{
```

- [x] **Étape 2 : remplacer la journalisation par une callback**

Juste après `public sealed class JournalSeal`, ajouter le niveau et le champ. Insérer l'énumération
**avant** la classe, dans le même namespace :

```csharp
    /// <summary>
    /// Severity of a message this class reports back to its host. Kept as an enum rather than
    /// folded into "exception or not": the three levels below already exist in both hosts' logs,
    /// and collapsing them would file a missing key — routine, the deck is simply unpaired — at the
    /// same level as a sealing failure.
    /// </summary>
    public enum JournalSealLevel { Info, Warn, Error }
```

Dans la classe, remplacer le bloc de champs privés

```csharp
        private readonly object _sync = new object();
        private readonly string _directory;
```

par

```csharp
        private readonly object _sync = new object();
        private readonly string _directory;

        /// <summary>
        /// Where this class says what happened. A callback rather than a direct logger call: the
        /// same file is compiled into the NinjaTrader add-on, which logs through SdLogger, and into
        /// the ATAS strategy, which logs through SdLog. Neither exists on the other side.
        /// Null is legal and means "say nothing" — losing a log line must never cost a seal.
        /// </summary>
        private readonly Action<JournalSealLevel, string, Exception> _report;
```

et le constructeur

```csharp
        public JournalSeal(string directory)
        {
            _directory = directory;
            LoadState();
        }
```

par

```csharp
        public JournalSeal(string directory, Action<JournalSealLevel, string, Exception> report = null)
        {
            _directory = directory;
            _report = report;
            LoadState();
        }

        /// <summary>Reports without ever being able to throw back into the capture path.</summary>
        private void Report(JournalSealLevel level, string message, Exception ex)
        {
            var report = _report;
            if (report == null) return;
            try { report(level, message, ex); }
            catch { /* a logger that throws must not cost the seal */ }
        }
```

- [x] **Étape 3 : remplacer les quatre appels à `SdLogger`**

Quatre appels, à remplacer un à un — la formulation des messages ne change pas, elle est déjà lue
dans les journaux existants :

```csharp
// dans Stamp(), branche catch :
SdLogger.Fail("Journal", ex, "Sealing failed — journal lines will go out unsigned");
// devient :
Report(JournalSealLevel.Error, "Sealing failed — journal lines will go out unsigned", ex);

// dans EnsureKey(), après le chargement réussi :
SdLogger.Info("Journal sealing key loaded — fills will be signed");
// devient :
Report(JournalSealLevel.Info, "Journal sealing key loaded — fills will be signed", null);

// dans EnsureKey(), branche catch :
SdLogger.Warn("Journal sealing key unreadable: {0}", ex.Message);
// devient :
Report(JournalSealLevel.Warn, "Journal sealing key unreadable", ex);

// dans LoadState(), branche catch :
SdLogger.Warn("Journal seal state unreadable — one chain break is expected: {0}", ex.Message);
// devient :
Report(JournalSealLevel.Warn, "Journal seal state unreadable — one chain break is expected", ex);
```

- [x] **Étape 4 : retirer la dépendance à `SimpleJson`**

Le fichier d'état est un objet à deux champs, écrit et relu par cette classe seule. Le lire avec un
analyseur JSON complet n'apportait rien et coûtait la seule dépendance qui empêche le partage.

Remplacer `LoadState()` et `SaveState()` par :

```csharp
        /// <summary>
        /// Reloads seq and sig from the state file.
        ///
        /// Hand-parsed rather than run through a JSON library: this file has exactly two fields, it
        /// is written by this class alone, and the library that used to parse it is the one
        /// dependency that stopped this file from being shared with the ATAS build. The parser is
        /// deliberately permissive — anything it cannot read leaves the state at zero, which costs
        /// ONE chain break and never a session.
        /// </summary>
        private void LoadState()
        {
            try
            {
                var path = StatePath();
                if (!File.Exists(path)) return;

                var text = File.ReadAllText(path);

                long seq;
                if (TryReadNumber(text, "seq", out seq)) _seq = seq;

                string sig;
                if (TryReadString(text, "sig", out sig)) _sig = sig;
            }
            catch (Exception ex)
            {
                // State lost: the next line breaks the chain once, the server resynchronises on it,
                // and everything after is sealed again. One damaged session, not a dead device.
                Report(JournalSealLevel.Warn, "Journal seal state unreadable — one chain break is expected", ex);
            }
        }

        /// <summary>Reads <c>"name":123</c>. Returns false on anything else, including a decimal.</summary>
        private static bool TryReadNumber(string text, string name, out long value)
        {
            value = 0;
            var at = IndexOfValue(text, name);
            if (at < 0) return false;

            var end = at;
            while (end < text.Length && (char.IsDigit(text[end]) || (end == at && text[end] == '-'))) end++;
            if (end == at) return false;

            return long.TryParse(text.Substring(at, end - at), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out value);
        }

        /// <summary>
        /// Reads <c>"name":"abc"</c>. No unescaping: the only string this file holds is a hex
        /// signature, which cannot contain a quote or a backslash.
        /// </summary>
        private static bool TryReadString(string text, string name, out string value)
        {
            value = null;
            var at = IndexOfValue(text, name);
            if (at < 0 || at >= text.Length || text[at] != '"') return false;

            var end = text.IndexOf('"', at + 1);
            if (end < 0) return false;

            value = text.Substring(at + 1, end - at - 1);
            return true;
        }

        /// <summary>Index of the first character AFTER <c>"name":</c>, or -1.</summary>
        private static int IndexOfValue(string text, string name)
        {
            var key = "\"" + name + "\"";
            var at = text.IndexOf(key, StringComparison.Ordinal);
            if (at < 0) return -1;

            at += key.Length;
            while (at < text.Length && (text[at] == ' ' || text[at] == ':')) at++;
            return at < text.Length ? at : -1;
        }

        private void SaveState()
        {
            try
            {
                Directory.CreateDirectory(_directory);

                // Built by hand for the same reason LoadState parses by hand. The signature is hex
                // by construction, so nothing here needs escaping — and the guard below makes that
                // an enforced fact rather than an assumption, because a stray quote would write a
                // state file this class could never read back.
                var sig = IsHex(_sig) ? _sig : string.Empty;
                var json = "{\"seq\":" + _seq.ToString(CultureInfo.InvariantCulture)
                         + ",\"sig\":\"" + sig + "\"}";

                File.WriteAllText(StatePath(), json, Encoding.UTF8);
            }
            catch
            {
                // Not blocking: at worst one chain break on the next start.
            }
        }

        private static bool IsHex(string value)
        {
            if (string.IsNullOrEmpty(value)) return true;
            for (var i = 0; i < value.Length; i++)
            {
                var c = value[i];
                var ok = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
                if (!ok) return false;
            }
            return true;
        }
```

- [x] **Étape 5 : brancher l'add-on NinjaTrader sur la nouvelle forme**

Dans `src/NinjaTrader.AddOn.StreamDeck/Services/ExecutionRecorder.cs`, ajouter le `using` en tête de
fichier, après `using NinjaTrader.Cbi;` :

```csharp
using TradeDeck.Journal;
```

puis remplacer la construction du sceau dans le constructeur :

```csharp
            _seal = new JournalSeal(_directory);
```

par

```csharp
            // The seal is shared verbatim with the ATAS strategy and knows nothing about either
            // host's logger, so it reports through a callback. The three levels map onto the ones
            // this file already used before the seal was made shareable.
            _seal = new JournalSeal(_directory, (level, message, ex) =>
            {
                if (level == JournalSealLevel.Error) SdLogger.Fail("Journal", ex, "{0}", message);
                else if (level == JournalSealLevel.Warn) SdLogger.Warn("{0}: {1}", message, ex != null ? ex.Message : "-");
                else SdLogger.Info("{0}", message);
            });
```

- [x] **Étape 6 : compiler les deux projets NinjaTrader**

```bash
dotnet build src/NinjaTrader.AddOn.StreamDeck/NinjaTrader.AddOn.StreamDeck.csproj -c Release
dotnet build src/NinjaTrader.Scripts/NinjaTrader.Scripts.csproj -c Release
```

Attendu : `Build succeeded` sur les deux, ~180 `CS0436` sur le premier. Toute erreur `CS0246` sur
`SimpleJson` ou `SdLogger` signale un appel oublié dans `JournalSeal.cs`.

- [x] **Étape 7 : vérifier que la signature n'a pas changé — contre l'autorité, pas contre soi-même**

La vraie référence n'est pas le comportement d'hier, c'est `deck-host/src/seal.ts`, dont ce fichier
doit rester le jumeau exact. On calcule la même signature des deux côtés sur le même enregistrement
et la même clé, et on compare.

Créer `<scratchpad>/seal-parity/parity.js` :

```javascript
// Twin of JournalSeal.Canonical / Stamp, transcribed from deck-host/src/seal.ts. If this file and
// the C# one stop agreeing, nothing seals any more — silently, because each side still believes it
// is signing correctly.
const { createHmac } = require('crypto');
const SEP = '\u001f';
const CHAMPS_EXEC = [
  'execId', 'orderId', 'account', 'instrument', 'marketPosition', 'price', 'quantity',
  'commission', 'pointValue', 'tickSize', 'orderName', 'trend', 'time', 'recordedAtUtc',
  'tradingDay',
];
function nombreCanonique(v) {
  if (v === null || v === undefined || v === '') return '';
  const n = Number(v);
  if (!Number.isFinite(n)) return '';
  return n.toFixed(8).replace(/\.?0+$/, '') || '0';
}
function texteCanonique(v) {
  if (v === null || v === undefined) return '';
  if (typeof v === 'boolean') return v ? 'true' : 'false';
  if (typeof v === 'number') return nombreCanonique(v);
  return String(v);
}
const record = {
  kind: 'exec', execId: 'E-1', orderId: 'O-1', account: 'Sim101', instrument: 'MNQ 09-26',
  marketPosition: 'Long', price: 5420.25, quantity: 2, commission: null, pointValue: 2,
  tickSize: 0.25, orderName: 'StreamDeck', time: '2026-08-24T14:03:02.0000000+02:00',
  recordedAtUtc: '2026-08-24T12:03:02.1230000Z', tradingDay: '2026-08-24',
};
const seq = 1756036982123;
const prevSig = '';
const canonical = ['exec', ...CHAMPS_EXEC.map((k) => texteCanonique(record[k]))].join(SEP);
const key = Buffer.from('a'.repeat(64), 'hex');
const sig = createHmac('sha256', key).update(`${seq}${SEP}${prevSig}${SEP}${canonical}`).digest('hex');
console.log('canonical=' + JSON.stringify(canonical));
console.log('sig=' + sig);
```

Créer le harnais C# jumeau `<scratchpad>/seal-parity/harness/harness.csproj` :

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>disable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <!-- The shared file itself, compiled here exactly as the ATAS project will compile it. This
         doubles as the proof that it is free of NinjaTrader dependencies. -->
    <Compile Include="C:\Github\streamDeckNT8\src\NinjaTrader.AddOn.StreamDeck\Utilities\JournalSeal.cs"
             Link="JournalSeal.cs" />
  </ItemGroup>
</Project>
```

et `<scratchpad>/seal-parity/harness/Program.cs` :

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using TradeDeck.Journal;

var dir = Path.Combine(Path.GetTempPath(), "seal-parity");
Directory.CreateDirectory(dir);
// A fixed key and no prior state: the signature must then be reproducible run after run.
File.WriteAllText(Path.Combine(dir, "journal.key"), new string('a', 64));
var statePath = Path.Combine(dir, "seal-state-exec.json");
if (File.Exists(statePath)) File.Delete(statePath);

var record = new Dictionary<string, object>
{
    ["kind"] = "exec",
    ["execId"] = "E-1",
    ["orderId"] = "O-1",
    ["account"] = "Sim101",
    ["instrument"] = "MNQ 09-26",
    ["marketPosition"] = "Long",
    ["price"] = 5420.25,
    ["quantity"] = 2,
    ["commission"] = null,
    ["pointValue"] = 2.0,
    ["tickSize"] = 0.25,
    ["orderName"] = "StreamDeck",
    ["time"] = "2026-08-24T14:03:02.0000000+02:00",
    ["recordedAtUtc"] = "2026-08-24T12:03:02.1230000Z",
    ["tradingDay"] = "2026-08-24",
};

var seal = new JournalSeal(dir, (level, message, ex) => Console.WriteLine($"[{level}] {message} {ex?.Message}"));
Console.WriteLine("active=" + seal.Active);
seal.Stamp(record);
Console.WriteLine("seq=" + record["seq"]);
Console.WriteLine("sig=" + record["sig"]);
```

Exécuter les deux. **Le `seq` de la version C# vient de l'horloge**, il ne peut pas coïncider avec
celui codé en dur dans le script Node : reporter le `seq` affiché par le harnais dans `parity.js`
(constante `seq`), relancer `node parity.js`, puis comparer les deux `sig`.

```bash
cd <scratchpad>/seal-parity/harness && dotnet run -c Release
node ../parity.js
```

Attendu : **les deux `sig` sont identiques**, à `seq` égal. S'ils diffèrent, la forme canonique a
bougé — ne pas continuer, c'est exactement l'incident que le commentaire en tête du fichier décrit.

- [x] **Étape 8 : noter l'obligation de version**

Ajouter la ligne suivante en tête de la section « Non publié » de `CHANGELOG.md` (créer la section
si elle n'existe pas, en respectant la forme des sections existantes) :

```markdown
- `JournalSeal.cs` passe au namespace neutre `TradeDeck.Journal` pour être partagé avec la stratégie
  ATAS. **Ce fichier est livré chez le trader** : la version installée doit être reconstruite
  (`docs/publier-une-version.md`), sinon `ExecutionRecorder` référence un namespace absent et la
  compilation NinjaScript échoue en bloc, indicateurs du trader compris.
```

- [x] **Étape 9 : committer**

```bash
git add src/NinjaTrader.AddOn.StreamDeck/Utilities/JournalSeal.cs \
        src/NinjaTrader.AddOn.StreamDeck/Services/ExecutionRecorder.cs \
        CHANGELOG.md binaries/
git commit -m "Rendre JournalSeal partageable avec la stratégie ATAS"
```

---

## Tâche 2 : le projet, le journal de bord, l'enveloppe et le client du pont

Fin de tâche : la stratégie se charge dans ATAS, démarre, et le bridge la voit comme add-on
connecté — **étape 1 de la recette**.

**Fichiers :**
- Créer : `src/Atas.Strategy.TradeDeck/Atas.Strategy.TradeDeck.csproj`
- Créer : `src/Atas.Strategy.TradeDeck/SdLog.cs`
- Créer : `src/Atas.Strategy.TradeDeck/BridgeMessage.cs`
- Créer : `src/Atas.Strategy.TradeDeck/BridgeClient.cs`
- Créer : `src/Atas.Strategy.TradeDeck/TradeDeckStrategy.cs`

**Interfaces :**
- Consomme : `TradeDeck.Journal.JournalSeal` (tâche 1) — seulement pour prouver que le lien MSBuild
  compile ; il n'est utilisé qu'en tâche 9.
- Produit : `TradeDeck.Atas.Strategy.SdLog` — `Trace/Debug/Info/Warn/Error(string, params object[])`,
  `Event/EventWarn(string category, string format, params object[] args)`,
  `TraceEvent(string category, string format, params object[] args)`,
  `Fail(string category, Exception ex, string format, params object[] args)`,
  `SessionHeader(string bridgeUrl)`.
- Produit : `TradeDeck.Atas.Strategy.BridgeMessage` — champs `Type/Version/RequestId/Timestamp/
  Source/Action/Payload/Result/Error`, `string ToJson()`, `static BridgeMessage FromJson(string)`,
  `static BridgeMessage Response(string requestId, string action, bool success, object result)`,
  `static BridgeMessage Failure(string requestId, string action, string code, string message)`,
  `static BridgeMessage Notify(string action, object payload)`,
  `string GetString(string key)`, `int? GetInt(string key)`, `double? GetDouble(string key)`,
  `bool GetBool(string key)`.
- Produit : `TradeDeck.Atas.Strategy.BridgeClient` — `BridgeClient(string url, int reconnectDelayMs)`,
  `void Start()`, `void Stop()`, `Task SendAsync(BridgeMessage)`, `bool IsConnected { get; }`,
  `event Action<BridgeMessage> MessageReceived`, `event Action<bool> ConnectionChanged`,
  `void Dispose()`.
- Produit : `TradeDeck.Atas.Strategy.TradeDeckStrategy : ChartStrategy`.

- [x] **Étape 1 : créer le projet**

> ⏸ Redémarrée : cette étape avait été exécutée et commitée en ciblant **ATAS X**, cible abandonnée
> le 24/08 (`docs/carte-des-deux-depots.md` non concerné, voir le préambule du plan). Le support
> ATAS a confirmé par écrit que les Chart Strategies n'existent pas sur ATAS X ; la cible est
> désormais **ATAS classique** (`C:\Program Files (x86)\ATAS Platform`). Seuls le commentaire et la
> valeur par défaut d'`ATAS_BASE` changent ci-dessous — `TargetFramework` reste `net10.0`, comme
> confirmé par la spec révisée. **Refait le 25/08** (commit `bfaa90f`) : le `.csproj` sur le disque
> cible désormais l'ATAS classique.

Créer `src/Atas.Strategy.TradeDeck/Atas.Strategy.TradeDeck.csproj` :

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <!--
    ATAS CLASSIQUE uniquement, PAS ATAS X : le support ATAS a confirme par ecrit que les Chart
    Strategies n'existent pas sur ATAS X (Beta), qui ne les prevoit pas avec date. Le dossier
    d'installation du classique est `C:\Program Files (x86)\ATAS Platform`, WPF, net10.0.
  -->
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <LangVersion>latest</LangVersion>
    <Nullable>annotations</Nullable>
    <UseWPF>false</UseWPF>
    <RootNamespace>TradeDeck.Atas.Strategy</RootNamespace>
    <AssemblyName>TradeDeck.Strategy</AssemblyName>
  </PropertyGroup>

  <PropertyGroup>
    <ATAS_BASE Condition="'$(ATAS_BASE)' == ''">C:\Program Files (x86)\ATAS Platform</ATAS_BASE>
  </PropertyGroup>

  <!--
    LE SCELLEMENT N'EST PAS COPIE ICI, IL EST LU LA-BAS.
    Un seul fichier sur le disque, deux compilateurs. Sa forme canonique doit rester identique au
    caractere pres a `deck-host/src/seal.ts` et a `Bitlearn/lib/tradeDeck/integrity.js` : une
    quatrieme implementation manuscrite serait le prochain incident.

    Le fichier reste physiquement dans le projet add-on parce que l'installateur ramasse
    `src/NinjaTrader.AddOn.StreamDeck/**/*.cs` pour la charge utile NinjaScript. Il reste donc en
    C# 9 / net48 : une construction plus recente compile ici et casse la compilation NinjaScript
    ENTIERE chez le trader.
  -->
  <ItemGroup>
    <Compile Include="..\NinjaTrader.AddOn.StreamDeck\Utilities\JournalSeal.cs"
             Link="Shared\JournalSeal.cs" />
  </ItemGroup>

  <ItemGroup>
    <Reference Include="ATAS.Indicators">
      <HintPath>$(ATAS_BASE)\ATAS.Indicators.dll</HintPath>
      <Private>false</Private>
    </Reference>
    <Reference Include="ATAS.Strategies">
      <HintPath>$(ATAS_BASE)\ATAS.Strategies.dll</HintPath>
      <Private>false</Private>
    </Reference>
    <Reference Include="ATAS.DataFeedsCore">
      <HintPath>$(ATAS_BASE)\ATAS.DataFeedsCore.dll</HintPath>
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
    <!--
      PAS de reference a StockSharp.Messages.dll : essaye puis retiree (tache 3). `Portfolio.
      ConnectionState` est bien un `Nullable<ConnectionStates>`, mais CE `ConnectionStates`-la vit
      dans Utils.Common.Network.Client, deja reference ci-dessus pour une autre raison, PAS dans
      StockSharp.Messages.dll — qui declare un type homonyme sans rapport. Une reference dessus fait
      echouer l'operateur `==` avec un CS0019 trompeur plutot que de resoudre le bon type. Constate
      en inspectant les assemblies reelles apres un premier build en echec, pas suppose.
    -->
    <!--
      ECART : Newtonsoft par HintPath et non par NuGet, pour eviter deux Newtonsoft dans le meme
      AppDomain que celui charge par ATAS. Version a verifier localement au premier build (l'ecart
      d'origine de ce commentaire supposait 13.0.3 par analogie avec ATAS X — cible abandonnee, voir
      le preambule du plan ; rien ne garantit que le classique livre la meme version).
      Repli si la version livree s'avere trop ancienne :
        <PackageReference Include="Newtonsoft.Json" Version="13.0.3" ExcludeAssets="runtime" />
    -->
    <Reference Include="Newtonsoft.Json">
      <HintPath>$(ATAS_BASE)\Newtonsoft.Json.dll</HintPath>
      <Private>false</Private>
    </Reference>
  </ItemGroup>

</Project>
```

- [x] **Étape 2 : compiler le projet vide pour valider les références**

> ⏸ Redémarrée avec l'étape 1 : le build exécuté visait `C:\Program Files\ATAS X`. À refaire contre
> `C:\Program Files (x86)\ATAS Platform` une fois le `.csproj` corrigé — les six DLL référencées
> (`ATAS.Indicators`, `ATAS.Strategies`, `ATAS.DataFeedsCore`, `OFT.Attributes`, `OFT.Localization`,
> `Utils.Common`) plus `Newtonsoft.Json` doivent exister à cet emplacement ; rien ne garantit qu'elles
> portent la même version que sur ATAS X. (`StockSharp.Messages` n'est plus référencée — voir le
> commentaire du `.csproj` ci-dessus.)
>
> **Refait le 25/08 contre l'installation classique** : les sept DLL existent,
> `OFT.Platform.runtimeconfig.json` annonce `tfm: net10.0` (+ `WindowsDesktop`) donc le
> `TargetFramework` tient, et `ATAS.DataFeedsCore.deps.json` annonce `Newtonsoft.Json 13.0.3` — même
> version que sur ATAS X, le repli `PackageReference` reste inutile. Build lancé **en `-c Debug` et
> non `-c Release`** : le Release réécrit `binaries/`, qui est versionné, et prouve exactement la même
> chose ici. `Build succeeded`, 0 erreur.

```bash
dotnet build src/Atas.Strategy.TradeDeck/Atas.Strategy.TradeDeck.csproj -c Release
```

Attendu : `Build succeeded`. Cette compilation ne contient encore que `JournalSeal.cs` : elle est la
**preuve que la tâche 1 a bien retiré toutes les dépendances NinjaTrader**. Une erreur `CS0246` ici
renvoie à la tâche 1, pas au projet.

- [x] **Étape 3 : écrire le journal de bord**

Créer `src/Atas.Strategy.TradeDeck/SdLog.cs` :

```csharp
using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;

namespace TradeDeck.Atas.Strategy;

/// <summary>
/// The strategy's log file, written to %APPDATA%\StreamDeckTrader\logs\addon-YYYY-MM-DD.log.
///
/// SAME PATH AND SAME LINE FORMAT AS THE NINJATRADER ADD-ON, deliberately. The diagnostic flow in
/// docs/logging-strategy.md correlates the three components' files by timestamp and by requestId;
/// a fourth file name, or a fifth column, would break every existing procedure for no gain. The
/// component column stays "addon" for the same reason: what changed is the platform underneath,
/// not the role in the protocol.
///
/// Files are UTF-8 WITH a BOM — the opposite of the journal spool. These are read by humans in
/// Notepad, VS Code and Get-Content, where a missing BOM mangles the accents.
/// </summary>
internal static class SdLog
{
    private const int LevelTrace = 0;
    private const int LevelDebug = 1;
    private const int LevelInfo = 2;
    private const int LevelWarn = 3;
    private const int LevelError = 4;

    private const string NoCategory = "-";

    /// <summary>
    /// Beyond this, the day's file is rolled. A single unbounded file becomes unopenable exactly
    /// on the day something went wrong badly enough to be worth reading.
    /// </summary>
    private const long MaxBytes = 32L * 1024 * 1024;

    private static readonly object Sync = new();

    /// <summary>
    /// TRACE is not written by default: the state publisher runs twice a second, and its frames
    /// alone would be hundreds of thousands of lines a day. Raised through TD_LOG_LEVEL when the
    /// whole WebSocket flow has to be replayed.
    /// </summary>
    private static int _fileLevel = ReadLevelFromEnvironment();

    private static StreamWriter? _writer;
    private static DateTime _openDate = DateTime.MinValue;
    private static int _rollIndex;
    private static string _currentPath = string.Empty;

    public static void Trace(string format, params object[] args) => Write(LevelTrace, NoCategory, Format(format, args), null);
    public static void Debug(string format, params object[] args) => Write(LevelDebug, NoCategory, Format(format, args), null);
    public static void Info(string format, params object[] args) => Write(LevelInfo, NoCategory, Format(format, args), null);
    public static void Warn(string format, params object[] args) => Write(LevelWarn, NoCategory, Format(format, args), null);
    public static void Error(string format, params object[] args) => Write(LevelError, NoCategory, Format(format, args), null);

    public static void Event(string category, string format, params object[] args) => Write(LevelInfo, category, Format(format, args), null);
    public static void EventWarn(string category, string format, params object[] args) => Write(LevelWarn, category, Format(format, args), null);
    public static void TraceEvent(string category, string format, params object[] args) => Write(LevelTrace, category, Format(format, args), null);
    public static void Fail(string category, Exception? ex, string format, params object[] args) => Write(LevelError, category, Format(format, args), ex);

    public static void SessionHeader(string bridgeUrl)
    {
        lock (Sync)
        {
            try { EnsureWriter(); } catch { CloseWriter(); }
        }

        Event("Session", "=== TradeDeck ATAS strategy session started === pid={0} assembly={1} bridge={2} logFile={3}",
            Process.GetCurrentProcess().Id,
            Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "?",
            bridgeUrl,
            string.IsNullOrEmpty(_currentPath) ? "(unavailable)" : _currentPath);
    }

    private static int ReadLevelFromEnvironment()
    {
        var raw = Environment.GetEnvironmentVariable("TD_LOG_LEVEL");
        return (raw ?? string.Empty).Trim().ToUpperInvariant() switch
        {
            "TRACE" => LevelTrace,
            "DEBUG" => LevelDebug,
            "INFO" => LevelInfo,
            "WARN" => LevelWarn,
            "ERROR" => LevelError,
            _ => LevelDebug,
        };
    }

    private static string Format(string format, object[] args)
    {
        if (args == null || args.Length == 0) return format;
        try { return string.Format(CultureInfo.InvariantCulture, format, args); }
        // A malformed format string must still produce a line, not swallow the event.
        catch (FormatException) { return format + " [unformattable args]"; }
    }

    private static void Write(int level, string category, string message, Exception? ex)
    {
        if (level < _fileLevel) return;

        lock (Sync)
        {
            try
            {
                var writer = EnsureWriter();
                if (writer == null) return;

                var line = new StringBuilder(256);
                line.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture))
                    .Append(" | ").Append(Label(level))
                    .Append(" | addon | ").Append(string.IsNullOrEmpty(category) ? NoCategory : category)
                    .Append(" | ").Append(Sanitize(message));

                if (ex != null)
                {
                    line.Append(" | exception=").Append(ex.GetType().Name)
                        .Append(" message=").Append(Sanitize(ex.Message));

                    foreach (var traceLine in ex.ToString().Split('\n'))
                    {
                        var trimmed = traceLine.TrimEnd('\r');
                        if (trimmed.Length > 0) line.Append("\n    ").Append(trimmed);
                    }
                }

                writer.WriteLine(line.ToString());
            }
            catch
            {
                // Drop the line and retry with a fresh handle next time (file moved, disk full,
                // permissions changed). Logging never propagates a failure to the caller — least of
                // all to an order path.
                CloseWriter();
            }
        }
    }

    private static string Label(int level) => level switch
    {
        LevelTrace => "TRACE",
        LevelDebug => "DEBUG",
        LevelInfo => "INFO ",
        LevelWarn => "WARN ",
        _ => "ERROR",
    };

    /// <summary>A newline inside a message would forge a second log line.</summary>
    private static string Sanitize(string value) =>
        string.IsNullOrEmpty(value) ? string.Empty : value.Replace("\r", " ").Replace("\n", " ");

    private static StreamWriter? EnsureWriter()
    {
        var today = DateTime.Now.Date;

        if (_writer != null && today != _openDate)
        {
            CloseWriter();
            _rollIndex = 0;
        }

        if (_writer != null && _writer.BaseStream.Length >= MaxBytes)
        {
            CloseWriter();
            _rollIndex++;
        }

        if (_writer != null) return _writer;

        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "StreamDeckTrader", "logs");
        Directory.CreateDirectory(directory);
        _openDate = today;

        var name = _rollIndex == 0
            ? string.Format(CultureInfo.InvariantCulture, "addon-{0:yyyy-MM-dd}.log", today)
            : string.Format(CultureInfo.InvariantCulture, "addon-{0:yyyy-MM-dd}.{1}.log", today, _rollIndex);

        _currentPath = Path.Combine(directory, name);

        // FileShare.ReadWrite: the file must stay readable while it is being written — the whole
        // point is to look at it while the problem is happening.
        var stream = new FileStream(_currentPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        _writer = new StreamWriter(stream, new UTF8Encoding(true)) { AutoFlush = true };
        return _writer;
    }

    private static void CloseWriter()
    {
        try { _writer?.Dispose(); } catch { /* nothing useful left to do */ }
        _writer = null;
    }
}
```

- [x] **Étape 4 : écrire l'enveloppe du protocole**

Créer `src/Atas.Strategy.TradeDeck/BridgeMessage.cs` :

```csharp
using System;
using System.Globalization;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace TradeDeck.Atas.Strategy;

internal sealed class MessageError
{
    [JsonProperty("code")] public string Code { get; set; } = string.Empty;
    [JsonProperty("message")] public string Message { get; set; } = string.Empty;
}

/// <summary>
/// The universal envelope of docs/protocol.md. Field names are lowerCamelCase on the wire and the
/// bridge is strict about them — hence the explicit [JsonProperty] on every one rather than a
/// naming strategy that a later refactor could quietly change.
/// </summary>
internal sealed class BridgeMessage
{
    [JsonProperty("type")] public string Type { get; set; } = string.Empty;
    [JsonProperty("version")] public string Version { get; set; } = "1.0";
    [JsonProperty("requestId", NullValueHandling = NullValueHandling.Ignore)] public string? RequestId { get; set; }
    [JsonProperty("timestamp", NullValueHandling = NullValueHandling.Ignore)] public string? Timestamp { get; set; }
    [JsonProperty("source", NullValueHandling = NullValueHandling.Ignore)] public string? Source { get; set; }
    [JsonProperty("action")] public string Action { get; set; } = string.Empty;
    [JsonProperty("payload", NullValueHandling = NullValueHandling.Ignore)] public JObject? Payload { get; set; }
    [JsonProperty("result", NullValueHandling = NullValueHandling.Ignore)] public JObject? Result { get; set; }
    [JsonProperty("error", NullValueHandling = NullValueHandling.Ignore)] public MessageError? Error { get; set; }

    private static readonly JsonSerializerSettings Settings = new()
    {
        NullValueHandling = NullValueHandling.Include,
        DateParseHandling = DateParseHandling.None,
    };

    public string ToJson() => JsonConvert.SerializeObject(this, Settings);

    /// <summary>Returns null on anything that is not a JSON object — never throws.</summary>
    public static BridgeMessage? FromJson(string json)
    {
        try { return JsonConvert.DeserializeObject<BridgeMessage>(json, Settings); }
        catch (JsonException) { return null; }
    }

    public static BridgeMessage Response(string? requestId, string action, bool success, object? result = null)
    {
        var payload = result == null ? new JObject() : JObject.FromObject(result);
        payload["success"] = success;

        return new BridgeMessage
        {
            Type = "response",
            RequestId = requestId,
            Source = "addon",
            Action = action,
            Timestamp = DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture),
            Result = payload,
        };
    }

    public static BridgeMessage Failure(string? requestId, string action, string code, string message) => new()
    {
        Type = "response",
        RequestId = requestId,
        Source = "addon",
        Action = action,
        Timestamp = DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture),
        Result = new JObject { ["success"] = false },
        Error = new MessageError { Code = code, Message = message },
    };

    public static BridgeMessage Notify(string action, object payload) => new()
    {
        Type = "event",
        Source = "addon",
        Action = action,
        Timestamp = DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture),
        Payload = JObject.FromObject(payload),
    };

    public string? GetString(string key) => Payload?[key]?.Type == JTokenType.String ? Payload[key]!.Value<string>() : null;

    /// <summary>
    /// NEVER THROWS ON A MALFORMED VALUE — it reads as absent instead.
    ///
    /// Same rule as the bridge's TryGetInt32: an exception in the validation path cost the whole
    /// session once, which the host reconnected immediately only to make it fall again — a
    /// permanent loop out of one bad field. A decimal is truncated rather than refused, matching
    /// SimpleJson.GetInt on the NinjaTrader side exactly; the bridge has already refused it with
    /// INVALID_PAYLOAD long before it gets here, so this is a second line, not the first.
    /// </summary>
    public int? GetInt(string key)
    {
        var token = Payload?[key];
        if (token == null) return null;
        try
        {
            return token.Type switch
            {
                JTokenType.Integer => (int)token.Value<long>(),
                JTokenType.Float => (int)token.Value<double>(),
                _ => null,
            };
        }
        catch (Exception) { return null; }
    }

    public double? GetDouble(string key)
    {
        var token = Payload?[key];
        if (token == null) return null;
        try
        {
            return token.Type is JTokenType.Integer or JTokenType.Float ? token.Value<double>() : null;
        }
        catch (Exception) { return null; }
    }

    /// <summary>
    /// Absent, null or malformed reads as FALSE. Deliberate: this backs the guard policy, and a
    /// payload nobody could understand must never be taken for permission to trade.
    /// </summary>
    public bool GetBool(string key)
    {
        var token = Payload?[key];
        if (token == null) return false;
        try
        {
            return token.Type switch
            {
                JTokenType.Boolean => token.Value<bool>(),
                JTokenType.String => string.Equals(token.Value<string>(), "true", StringComparison.OrdinalIgnoreCase),
                _ => false,
            };
        }
        catch (Exception) { return false; }
    }
}
```

- [x] **Étape 5 : écrire le client du pont**

Créer `src/Atas.Strategy.TradeDeck/BridgeClient.cs` :

```csharp
using System;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace TradeDeck.Atas.Strategy;

/// <summary>
/// WebSocket client to the bridge, with its own reconnection loop. The bridge is the only server on
/// the trading path; this side is a client that comes back on its own, exactly like the NinjaTrader
/// add-on it replaces. No handshake: connecting to the add-on port IS the registration.
/// </summary>
internal sealed class BridgeClient : IDisposable
{
    private readonly string _url;
    private readonly int _reconnectDelayMs;

    /// <summary>
    /// THE SEND LOCK IS NOT OPTIONAL, AND IT SURVIVED THE PORT.
    ///
    /// ClientWebSocket allows exactly ONE outstanding SendAsync on .NET 10, as it did on .NET
    /// Framework 4.8. What changed is the punishment: 4.8 aborted the socket, .NET 10 throws. The
    /// reason the guard exists has not changed at all — the state publisher's timer and the receive
    /// loop both send, and a lost frame is a lost order confirmation.
    /// </summary>
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    private ClientWebSocket? _ws;
    private CancellationTokenSource? _cts;
    private bool _running;
    private bool _disposed;

    public event Action<BridgeMessage>? MessageReceived;
    public event Action<bool>? ConnectionChanged;

    public bool IsConnected => _ws is { State: WebSocketState.Open };

    public BridgeClient(string url, int reconnectDelayMs)
    {
        _url = url;
        _reconnectDelayMs = reconnectDelayMs;
    }

    public void Start()
    {
        if (_running) return;
        _running = true;
        _cts = new CancellationTokenSource();
        _ = Task.Run(() => ConnectLoop(_cts.Token));
        SdLog.Info("Bridge client started, connecting to {0}", _url);
    }

    public void Stop()
    {
        _running = false;
        _cts?.Cancel();
        CloseSocket();
        SdLog.Info("Bridge client stopped");
    }

    private async Task ConnectLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                // Dispose the previous socket before replacing it, otherwise every reconnect leaks
                // a ClientWebSocket for the life of the session.
                var previous = _ws;
                if (previous != null)
                {
                    _ws = null;
                    try { previous.Dispose(); } catch { /* already gone */ }
                }

                _ws = new ClientWebSocket();
                await _ws.ConnectAsync(new Uri(_url), ct);
                SdLog.Event("Connection", "Connected to bridge at {0}", _url);
                ConnectionChanged?.Invoke(true);

                await ReceiveLoop(ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                // Type included: a SocketException (bridge not started) and a WebSocketException
                // (bridge died mid-session) call for different fixes.
                SdLog.EventWarn("Connection", "Bridge connection lost: {0} — {1}", ex.GetType().Name, ex.Message);
                ConnectionChanged?.Invoke(false);
            }

            if (ct.IsCancellationRequested) break;

            SdLog.Info("Reconnecting in {0}ms...", _reconnectDelayMs);
            try { await Task.Delay(_reconnectDelayMs, ct); }
            catch (OperationCanceledException) { break; }
        }

        ConnectionChanged?.Invoke(false);
    }

    private async Task ReceiveLoop(CancellationToken ct)
    {
        var buffer = new byte[8192];
        var frame = new StringBuilder();

        while (_ws is { State: WebSocketState.Open } && !ct.IsCancellationRequested)
        {
            var result = await _ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);

            if (result.MessageType == WebSocketMessageType.Close)
            {
                SdLog.Event("Connection", "Bridge closed the connection");
                break;
            }

            frame.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
            if (!result.EndOfMessage) continue;

            var json = frame.ToString();
            frame.Clear();

            try
            {
                var message = BridgeMessage.FromJson(json);
                if (message == null)
                {
                    SdLog.EventWarn("Wire", "Bridge frame ignored — not a JSON object: {0}", json);
                    continue;
                }

                SdLog.Debug("Received: {0} / {1} {2}", message.Type, message.Action, json);
                MessageReceived?.Invoke(message);
            }
            catch (Exception ex)
            {
                SdLog.Fail("Wire", ex, "Invalid frame from bridge: {0}", json);
            }
        }
    }

    public async Task SendAsync(BridgeMessage message)
    {
        var socket = _ws;
        if (socket is not { State: WebSocketState.Open })
        {
            SdLog.Warn("Cannot send — not connected to bridge");
            return;
        }

        await _sendLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (socket.State != WebSocketState.Open)
            {
                SdLog.Warn("Cannot send — socket closed while waiting for the send lock");
                return;
            }

            var json = message.ToJson();
            var bytes = Encoding.UTF8.GetBytes(json);
            await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None)
                .ConfigureAwait(false);

            // State goes out twice a second and would bury everything else; it stays available one
            // level down, with the serialized frame for replay.
            if (message.Action == "stateUpdate")
                SdLog.TraceEvent("Wire", "Sent stateUpdate: {0}", json);
            else
                SdLog.Debug("Sent: {0} / {1} [REQ:{2}] {3}", message.Type, message.Action, message.RequestId ?? "n/a", json);
        }
        catch (Exception ex)
        {
            // Swallowed on purpose: every caller is either an event handler of the connector or a
            // fire-and-forget publish, and neither can do anything with the failure.
            SdLog.Fail("Wire", ex, "Failed to send {0}/{1} to bridge", message.Type, message.Action);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private void CloseSocket()
    {
        try
        {
            if (_ws is { State: WebSocketState.Open })
            {
                _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Shutting down", CancellationToken.None)
                    .GetAwaiter().GetResult();
            }
        }
        catch { /* the socket is going away either way */ }
        finally
        {
            _ws?.Dispose();
            _ws = null;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        _cts?.Dispose();
        _sendLock.Dispose();
    }
}
```

- [x] **Étape 6 : écrire le point d'entrée**

Créer `src/Atas.Strategy.TradeDeck/TradeDeckStrategy.cs`. Cette version est **volontairement
minimale** : elle ouvre la WebSocket et répond `UNSUPPORTED_ACTION` à tout. Les tâches suivantes la
complètent.

```csharp
using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;

using ATAS.Strategies.Chart;

namespace TradeDeck.Atas.Strategy;

/// <summary>
/// The deck's foothold inside ATAS. Replaces the NinjaTrader add-on: same protocol, same bridge,
/// same log file — a different platform underneath.
///
/// THE CHART IS ONLY A HOST. ATAS requires a strategy to be attached to a chart and started, but
/// nothing here reads that chart: the deck's selection is resolved against the host's own connector
/// registry (task 3 — ChartStrategy.Connector is confirmed always null on classic ATAS, never used)
/// and set EXPLICITLY on every order. The chart may display anything at all.
///
/// A CLOSED CHART BREAKS NOTHING NEW. Stopped or removed, the strategy simply stops being a client
/// of the bridge, and the bridge reports "add-on disconnected" exactly as it does today when
/// NinjaTrader is closed. The deck already shows that state.
/// </summary>
[DisplayName("TradeDeck")]
[Display(Name = "TradeDeck",
    Description = "Surface de controle Stream Deck : execute les commandes du deck via le bridge local.")]
public class TradeDeckStrategy : ChartStrategy
{
    private const string BridgeUrl = "ws://127.0.0.1:8219";
    private const int ReconnectDelayMs = 3000;

    private BridgeClient? _bridge;

    public TradeDeckStrategy() : base(true)
    {
    }

    /// <summary>
    /// Nothing to compute. This strategy is driven by the deck and by the connector's events, not
    /// by bars — the override exists because the base class requires it.
    /// </summary>
    protected override void OnCalculate(int bar, decimal value)
    {
    }

    protected override void OnStarted()
    {
        try
        {
            SdLog.SessionHeader(BridgeUrl);

            _bridge = new BridgeClient(BridgeUrl, ReconnectDelayMs);
            _bridge.MessageReceived += OnBridgeMessage;
            _bridge.ConnectionChanged += OnConnectionChanged;
            _bridge.Start();

            SdLog.Event("Session", "Strategy started — bridge={0}", BridgeUrl);
        }
        catch (Exception ex)
        {
            SdLog.Fail("Session", ex, "Failed to start the TradeDeck strategy");
            // FAIL LOUDLY. A cockpit that is silently absent is worse than one that is plainly
            // broken: the trader would keep pressing keys and keep believing orders were leaving.
            StopWithNotification("TradeDeck n'a pas pu demarrer : " + ex.Message);
        }
    }

    /// <summary>
    /// The chart carrying the strategy was closed. ATAS suspends rather than stops.
    ///
    /// The socket MUST go down here. Left open, the bridge would keep reporting "add-on connected"
    /// while nothing behind it can act: the deck would show a live cockpit whose keys quietly do
    /// nothing, which is the one failure mode worse than showing it disconnected.
    /// </summary>
    protected override void OnSuspended()
    {
        SdLog.EventWarn("Session", "Strategy suspended (chart closed) — dropping the bridge connection");
        try { _bridge?.Stop(); }
        catch (Exception ex) { SdLog.Fail("Session", ex, "Error while stopping the bridge client on suspend"); }
    }

    /// <summary>
    /// WE DELIBERATELY DO NOT CLOSE POSITIONS HERE.
    ///
    /// ATAS's own guidance is the opposite — "it is extremely desirable to redefine the OnStopping
    /// method and realize the logic of closing internal positions and cancelling the posted orders"
    /// — and it is right for an automated strategy, whose positions are its own and meaningless once
    /// it stops. This is not one. The deck is a CONTROL SURFACE: the positions are the trader's, and
    /// liquidating them because a chart was closed, or because the strategy was restarted after a
    /// recompile, would be the single most destructive thing this code could do.
    /// </summary>
    protected override void OnStopping()
    {
        try { _bridge?.Stop(); }
        catch (Exception ex) { SdLog.Fail("Session", ex, "Error while stopping the bridge client"); }
    }

    protected override void OnStopped()
    {
        try
        {
            if (_bridge != null)
            {
                _bridge.MessageReceived -= OnBridgeMessage;
                _bridge.ConnectionChanged -= OnConnectionChanged;
                _bridge.Dispose();
                _bridge = null;
            }

            SdLog.Event("Session", "=== TradeDeck ATAS strategy session ended ===");
        }
        catch (Exception ex)
        {
            SdLog.Fail("Session", ex, "Error during shutdown");
        }
    }

    private void OnConnectionChanged(bool connected)
    {
        if (connected) SdLog.Event("Connection", "Connected to bridge at {0}", BridgeUrl);
        else SdLog.EventWarn("Connection", "Disconnected from bridge — will retry every {0}ms", ReconnectDelayMs);
    }

    private void OnBridgeMessage(BridgeMessage message)
    {
        if (message.Type != "command")
        {
            SdLog.Debug("Ignoring non-command message: {0}/{1}", message.Type, message.Action);
            return;
        }

        SdLog.Event("Command", "[REQ:{0}] Received {1} payload={2}",
            message.RequestId ?? "n/a", message.Action,
            message.Payload?.ToString(Newtonsoft.Json.Formatting.None) ?? "{}");

        var response = BridgeMessage.Failure(message.RequestId, message.Action,
            "UNSUPPORTED_ACTION", $"Action '{message.Action}' is not supported yet by the ATAS strategy.");

        // Fire-and-forget: this runs on the connector's thread and must not block. SendAsync
        // catches its own errors, so no faulted task is left unobserved.
        _ = _bridge?.SendAsync(response);
    }
}
```

- [x] **Étape 7 : compiler**

```bash
dotnet build src/Atas.Strategy.TradeDeck/Atas.Strategy.TradeDeck.csproj -c Release
```

Attendu : `Build succeeded`, et une ligne
`binaries\Atas.Strategy.TradeDeck\ ← TradeDeck.Strategy.dll, TradeDeck.Strategy.pdb, …`.

- [ ] **Étape 8 : charger la stratégie dans ATAS et vérifier — recette étape 1**

> ⏸ Non exécutée à l'origine (exigeait ATAS ouvert et le bridge lancé). Depuis, l'essai terrain du
> 24/08 a chargé la stratégie sur **ATAS classique** et validé cette étape (recette A/B1, voir
> `.superpowers/sdd/2026-08-24-portage-chemin-ordre-atas/progress.md`) — mais avec un binaire déployé
> à la main pendant l'investigation, pas via ce protocole écrit ATAS X → classique. À rejouer avec le
> `.csproj` corrigé (étape 1) pour que la procédure ci-dessous soit celle réellement suivie.

**Cible : ATAS classique, PAS ATAS X.** Deux différences avec ATAS X qui piègent si on les ignore :
le classique **ne surveille pas** `%APPDATA%\ATAS\Strategies\` — déposer le DLL ne suffit pas, **ATAS
doit être redémarré** pour le charger, contrairement à ATAS X qui recompile à la volée comme
NinjaTrader ouvert (voir « Déploiement » dans `CLAUDE.md`) ; et la stratégie se charge par **clic
droit sur le graphique → *Chart strategies*** (pas un menu global *Stratégies*).

1. Copier le DLL :
   ```powershell
   Copy-Item src\Atas.Strategy.TradeDeck\bin\Release\net10.0\TradeDeck.Strategy.dll `
             "$env:APPDATA\ATAS\Strategies\" -Force
   ```
2. **Fermer NinjaTrader** (un seul add-on à la fois).
3. Lancer le bridge sur ports isolés :
   ```powershell
   $env:SDBRIDGE_PluginPort = "9318"; $env:SDBRIDGE_AddonPort = "9319"
   $env:SDBRIDGE_LogDirectory = "<scratchpad>\logtest"
   dotnet run --project src\StreamDeckBridge\StreamDeckBridge.csproj -c Release
   ```
   ⚠ Le port du bridge est 9319 pour ce test, mais `BridgeUrl` est codé sur 8219. Pour cet essai,
   modifier temporairement la constante — **ou**, mieux, lancer le bridge sur ses ports par défaut
   avec NinjaTrader fermé et l'hôte TradeDeck arrêté. Choisir la seconde option quand la machine est
   libre ; la première seulement si une installation tourne.
4. **Redémarrer ATAS classique** (obligatoire après tout dépôt de DLL — voir ci-dessus). Ouvrir un
   graphique quelconque avec un portefeuille sélectionné dans ses paramètres et un flux connecté
   (ATAS refuse de démarrer la stratégie sinon), clic droit sur le graphique → *Chart strategies* →
   ajouter **TradeDeck** → *Démarrer*.

Attendu, **et chaque point doit être constaté, pas supposé** :
- `%APPDATA%\StreamDeckTrader\logs\addon-AAAA-MM-JJ.log` contient la ligne
  `=== TradeDeck ATAS strategy session started === pid=… bridge=ws://127.0.0.1:8219 …` ;
- puis `Connection | Connected to bridge at ws://127.0.0.1:8219` ;
- le journal du bridge affiche `NinjaTrader Add-On connected` ;
- arrêter la stratégie fait apparaître `NinjaTrader Add-On disconnected` côté bridge et
  `=== TradeDeck ATAS strategy session ended ===` côté stratégie.

**Piège à connaître avant de diagnostiquer un silence** : ATAS classique **arrête la stratégie sans
message** dès que l'instrument, l'unité de temps ou le portefeuille du graphique changent, que
l'historique recharge, que le graphique se ferme, ou qu'on redépose le DLL. Un flux `stateUpdate` qui
s'interrompt est d'abord ce cas-là, pas un bug — vérifier l'état de la stratégie dans ATAS avant de
creuser plus loin.

- [x] **Étape 9 : committer**

```bash
git add src/Atas.Strategy.TradeDeck/ binaries/
git commit -m "Creer la strategie ATAS TradeDeck : projet, journal, enveloppe et client du pont"
```

---

## Tâche 3 : le registre de connecteurs, la résolution du contexte et la publication d'état

Fin de tâche : le deck affiche le bon compte, le bon instrument et une position vide — **étape 2 de
la recette**. Le bloc `position` est **complet dès maintenant**, protections comprises : le protocole
déclare ces champs requis, et les omettre ferait clignoter la signature de protection entre `-` et
un nombre à chaque ouverture.

> ⚠ **Cette tâche a déjà été exécutée et commitée une première fois (commits `9053905`..`25e91b7`),
> contre une conception invalidée depuis par l'essai terrain du 24/08.** L'implémentation livrée
> résolvait `Portfolio`/`Security`/`Order`/`Position` contre `ChartStrategy.Connector` — mesuré
> **toujours nul** sur 472 trames publiées, confirmé par écrit par le support ATAS (« the connector
> must be assigned manually if you need it »). Ce n'est pas un ajustement : `ContextResolver` et
> `StatePublisher` doivent être **réécrits** contre le **registre de connecteurs de l'hôte**
> (`OFT.Platform.Core`, atteint par réflexion via `CommonServiceLocator`), agrégé sur
> `IConnectorsManager.Connectors` — avec repli vers `TradingManager` (mode dégradé) quand ce registre
> est introuvable. Les étapes concernées sont détaillées ci-dessous et remises à `- [ ]`.

**Fichiers :**
- Créer : `src/Atas.Strategy.TradeDeck/ConnectorRegistry.cs`
- Créer : `src/Atas.Strategy.TradeDeck/ContextResolver.cs`
- Créer : `src/Atas.Strategy.TradeDeck/StatePublisher.cs`
- Modifier : `src/Atas.Strategy.TradeDeck/TradeDeckStrategy.cs`

**Interfaces :**
- Consomme : `BridgeClient.SendAsync`, `BridgeMessage.Notify/Response/Failure`, `SdLog` (tâche 2).
- Produit : `ConnectorRegistry` —
  `ConnectorRegistry()`, `bool IsAvailable { get; }`,
  `IReadOnlyList<IDataFeedConnector> GetConnectors()`,
  `IDataFeedConnector? ConnectorFor(Portfolio? portfolio)`.
  Entièrement par réflexion, sans référence de compilation à `OFT.Platform.Core`, `OFT.Platform` ni
  `CommonServiceLocator` (voir Contraintes globales) — le seul type qui traverse la frontière est
  `ATAS.DataFeedsCore.IDataFeedConnector`, déjà référencé par le projet.
- Produit : `ContextResolver` —
  `ContextResolver(ConnectorRegistry registry, Func<ITradingManager?> tradingManager)`,
  `bool IsDegraded { get; }`, `IReadOnlyList<IDataFeedConnector> Connectors { get; }`,
  `Portfolio? FindPortfolio(string? name)`, `Security? FindSecurity(string? name)`,
  `Position? FindPosition(Portfolio?, Security?)`, `List<Position> FindAllPositions(Portfolio?)`,
  `List<string> GetAccountNames()`,
  `List<Order> FindActiveOrders(Portfolio?, Security?)`,
  `List<Order> FindStopOrders(Portfolio?, Security?)`,
  `List<Order> FindTargetOrders(Portfolio?, Security?)`,
  `List<Order> FindAllActiveOrders(Portfolio?)`,
  `IDataFeedConnector? ConnectorFor(Portfolio? portfolio)`,
  `static string InstrumentName(Security?)`, `static decimal PointValue(Security?)`,
  `static decimal MarketPrice(Security?)`, `static bool IsActive(Order?)`,
  `static bool SamePortfolio(Portfolio?, Portfolio?)`, `static bool SameSecurity(Security?, Security?)`.
  **Plus de propriété `Connector` unique** : `ChartStrategy.Connector` est toujours nul, et le
  registre agrège potentiellement plusieurs connecteurs — voir l'étape 2.
- Produit : `StatePublisher` —
  `StatePublisher(ContextResolver, BridgeClient, int intervalMs)`,
  `void Start(string account, string instrument)`, `void UpdateTracking(string?, string?)`,
  `void PublishNow()`, `string TrackedAccount { get; }`, `string TrackedInstrument { get; }`,
  `void Dispose()`.

- [x] **Étape 1 : écrire l'accès au registre de connecteurs de l'hôte, par réflexion**

> Nouvelle étape — c'est le maillon qui manquait à la première exécution de cette tâche. Elle
> implémente proprement `prototype-acces-connecteur.patch`
> (`.superpowers/sdd/2026-08-24-portage-chemin-ordre-atas/`), qui est du code d'investigation à ne
> **pas** recopier tel quel : il journalise à outrance (utile pour établir les faits une fois, pas
> pour tourner en production sur la boucle 500 ms) et vit dans `StatePublisher` alors que cette
> responsabilité — obtenir et mettre en cache le registre — appartient à sa propre classe, réutilisée
> par `ContextResolver`, `OrderMonitor` (tâche 4) et `GuardEnforcer` (tâche 8).

Créer `src/Atas.Strategy.TradeDeck/ConnectorRegistry.cs` :

```csharp
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

using ATAS.DataFeedsCore;

namespace TradeDeck.Atas.Strategy;

/// <summary>
/// Reaches the platform's own connector registry — the thing that makes multi-account selection
/// possible on classic ATAS, since <see cref="ATAS.Strategies.Chart.ChartStrategy.Connector"/> is
/// confirmed permanently null (measured across 472 published frames, confirmed in writing by ATAS
/// support: "the connector must be assigned manually if you need it").
///
/// ENTIRELY BY REFLECTION, NO COMPILE-TIME REFERENCE to OFT.Platform.Core, OFT.Platform or
/// CommonServiceLocator. These are host internals, not ATAS's documented plugin surface: a hard
/// reference means the strategy stops loading AT ALL the day one of them is renamed — a dead cockpit
/// over a feature the trader never asked to lose. By reflection, a rename instead costs one field
/// (IsAvailable flips to false) and this class degrades quietly; ContextResolver then falls back to
/// TradingManager, chart-scoped, and says so once in the log — see ContextResolver.
///
/// Verified live 2026-08-24 (see progress.md, "SONDE SERVICELOCATOR"): IsLocationProviderSet=True,
/// ITradingCore and IConnectorsManager both resolve, GetConnector(DEMO29ADC) returned a live
/// SbeConnector (Portfolios=1 Securities=5 Orders=2 Positions=2 MyTrades=1), and
/// IConnectorsManager.Connectors listed 12 connectors, 2 of them carrying a portfolio (10 configured
/// but never connected).
/// </summary>
internal sealed class ConnectorRegistry
{
    // STATIC AND RESOLVED ONCE. This class answers GetConnectors() on the 500ms state-publish tick —
    // hundreds of thousands of calls a day. CommonServiceLocator.ServiceLocator.Current,
    // ITradingCore and IConnectorsManager never change identity while ATAS runs, so the reflection
    // LOOKUPS (assembly scan, GetProperty, GetMethod) are paid once for the process, never per tick.
    // What IS re-read on every call is the DATA — Connectors, Portfolios — because those genuinely
    // change: a broker connects, another disconnects, mid-session.
    private static readonly object InitLock = new();
    private static bool _initAttempted;
    private static object? _tradingCore;          // ITradingCore instance, optional fast path
    private static object? _connectorsManager;     // IConnectorsManager instance — load-bearing
    private static PropertyInfo? _connectorsProperty;
    private static MethodInfo? _getConnectorByPortfolio;
    private static string? _initFailureReason;

    /// <summary>Transition-tracked, not read every tick: see ReportAvailability.</summary>
    private bool? _lastAvailable;

    public ConnectorRegistry() => EnsureInitialized();

    public bool IsAvailable => _connectorsManager != null && _connectorsProperty != null;

    /// <summary>
    /// Every connector currently carrying at least one portfolio.
    ///
    /// FILTERED ON PURPOSE: of the 12 connectors measured live, 10 were configured but never
    /// connected and carried zero portfolios. Presenting them as selectable accounts would let the
    /// deck resolve a name against a connector that can never fill an order.
    ///
    /// Re-enumerated on every call — see the static fields' comment above for why the cost of doing
    /// so is acceptable and the cost of NOT doing so would be a stale account list.
    /// </summary>
    public IReadOnlyList<IDataFeedConnector> GetConnectors()
    {
        if (!IsAvailable)
        {
            ReportAvailability(false, _initFailureReason ?? "registry not initialized");
            return Array.Empty<IDataFeedConnector>();
        }

        try
        {
            var raw = _connectorsProperty!.GetValue(_connectorsManager) as IEnumerable;
            var result = new List<IDataFeedConnector>();
            if (raw != null)
            {
                foreach (var entry in raw)
                {
                    if (entry is IDataFeedConnector connector && SafePortfolioCount(connector) > 0)
                        result.Add(connector);
                }
            }

            ReportAvailability(true, null);
            return result;
        }
        catch (Exception ex)
        {
            ReportAvailability(false, ex.GetType().Name + ": " + ex.Message);
            return Array.Empty<IDataFeedConnector>();
        }
    }

    /// <summary>
    /// The connector that owns this portfolio, or null. Point 4 of the field trial: an Order, a
    /// Position and a MyTrade each belong to a connector — watching, cancelling or modifying them
    /// must act on the one that owns the pair, never on a global connector (there is none).
    ///
    /// Tries the host's own lookup first (measured live: GetConnector(DEMO29ADC) -> SbeConnector,
    /// exact match), falling back to a linear scan of GetConnectors() so this method stays correct
    /// even when ITradingCore itself could not be resolved but IConnectorsManager could.
    /// </summary>
    public IDataFeedConnector? ConnectorFor(Portfolio? portfolio)
    {
        if (portfolio == null) return null;

        if (_tradingCore != null && _getConnectorByPortfolio != null)
        {
            try
            {
                if (_getConnectorByPortfolio.Invoke(_tradingCore, new object?[] { portfolio }) is IDataFeedConnector fast)
                    return fast;
            }
            catch { /* fall through to the scan below — this path is a shortcut, not load-bearing */ }
        }

        foreach (var connector in GetConnectors())
        {
            if (SafeContainsPortfolio(connector, portfolio)) return connector;
        }
        return null;
    }

    /// <summary>
    /// Logged ONLY on a transition, never per tick — this is read from the 500ms publish loop and
    /// CLAUDE.md is explicit that one line there is hundreds of thousands a day. A registry that
    /// never becomes available must still be visible once, at a level a normal run shows; that is
    /// exactly the degraded-mode entry ContextResolver relies on this class to report honestly.
    /// </summary>
    private void ReportAvailability(bool available, string? reason)
    {
        if (_lastAvailable == available) return;
        _lastAvailable = available;

        if (available)
        {
            SdLog.Event("Registry", "Connector registry reachable — resolving deck accounts against IConnectorsManager.Connectors");
        }
        else
        {
            SdLog.EventWarn("Registry",
                "Connector registry unreachable ({0}) — degrading to the chart's own account and instrument via TradingManager",
                reason ?? "?");
        }
    }

    private static int SafePortfolioCount(IDataFeedConnector connector)
    {
        try { return connector.Portfolios?.Count() ?? 0; }
        catch { return 0; }
    }

    private static bool SafeContainsPortfolio(IDataFeedConnector connector, Portfolio portfolio)
    {
        try
        {
            var portfolios = connector.Portfolios;
            if (portfolios == null) return false;
            foreach (var p in portfolios)
            {
                if (ReferenceEquals(p, portfolio)) return true;
                if (!string.IsNullOrEmpty(p?.AccountID) &&
                    string.Equals(p.AccountID, portfolio.AccountID, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }
        catch { return false; }
    }

    /// <summary>
    /// Resolves CommonServiceLocator -> ITradingCore / IConnectorsManager once, by assembly simple
    /// name and full type name. EVERY STEP OWNS ITS OWN FAILURE PATH: the worst outcome of any of
    /// them failing is IsAvailable staying false, never an exception escaping to the caller — this
    /// runs from a strategy's OnStarted, and a constructor that throws would refuse to load the
    /// whole cockpit over a diagnostic nobody asked for.
    /// </summary>
    private static void EnsureInitialized()
    {
        lock (InitLock)
        {
            if (_initAttempted) return;
            _initAttempted = true;

            try
            {
                var assemblies = AppDomain.CurrentDomain.GetAssemblies();

                var locatorType = FindType(assemblies, "CommonServiceLocator", "CommonServiceLocator.ServiceLocator");
                if (locatorType == null) { _initFailureReason = "CommonServiceLocator.ServiceLocator not loaded"; return; }

                var isSet = locatorType.GetProperty("IsLocationProviderSet", BindingFlags.Public | BindingFlags.Static)?.GetValue(null) as bool?;
                if (isSet != true) { _initFailureReason = "IsLocationProviderSet=false"; return; }

                var current = locatorType.GetProperty("Current", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
                if (current == null) { _initFailureReason = "ServiceLocator.Current returned null"; return; }

                var getInstance = current.GetType().GetMethod("GetInstance", new[] { typeof(Type) });
                if (getInstance == null) { _initFailureReason = "IServiceLocator.GetInstance(Type) not found"; return; }

                // ITradingCore is a nice-to-have shortcut (GetConnector(Portfolio)), not
                // load-bearing: GetConnectors() below works from IConnectorsManager alone. A failure
                // here does not abort initialisation.
                var tradingCoreType = FindType(assemblies, "OFT.Platform.Core", "OFT.Platform.Core.Providers.ITradingCore");
                if (tradingCoreType != null)
                {
                    try
                    {
                        _tradingCore = getInstance.Invoke(current, new object?[] { tradingCoreType });
                        _getConnectorByPortfolio = _tradingCore?.GetType().GetMethod("GetConnector", new[] { typeof(Portfolio) });
                    }
                    catch { _tradingCore = null; _getConnectorByPortfolio = null; }
                }
                // CoreProvider.TradingCore is confirmed NULL live (progress.md, step 4 of the probe) —
                // deliberately not attempted as a second path to ITradingCore.

                var managerType = FindType(assemblies, "OFT.Platform.Core", "OFT.Platform.Core.Managers.Connections.IConnectorsManager");
                if (managerType == null) { _initFailureReason = "IConnectorsManager type not found"; return; }

                object? manager;
                try { manager = getInstance.Invoke(current, new object?[] { managerType }); }
                catch (Exception ex)
                {
                    var real = ex is TargetInvocationException { InnerException: { } inner } ? inner : ex;
                    _initFailureReason = "GetInstance(IConnectorsManager) threw " + real.GetType().Name;
                    return;
                }
                if (manager == null) { _initFailureReason = "GetInstance(IConnectorsManager) returned null"; return; }

                var connectorsProperty = manager.GetType().GetProperty("Connectors", BindingFlags.Public | BindingFlags.Instance);
                if (connectorsProperty == null) { _initFailureReason = "IConnectorsManager.Connectors property not found"; return; }

                _connectorsManager = manager;
                _connectorsProperty = connectorsProperty;
            }
            catch (Exception ex)
            {
                _initFailureReason = ex.GetType().Name + ": " + ex.Message;
            }
        }
    }

    private static Type? FindType(Assembly[] assemblies, string assemblySimpleName, string fullTypeName)
    {
        foreach (var assembly in assemblies)
        {
            string? name;
            try { name = assembly.GetName().Name; } catch { continue; }
            if (!string.Equals(name, assemblySimpleName, StringComparison.OrdinalIgnoreCase)) continue;

            try
            {
                var type = assembly.GetType(fullTypeName, throwOnError: false);
                if (type != null) return type;
            }
            catch { /* keep scanning the other assemblies matching this simple name, if any */ }
        }
        return null;
    }
}
```

- [x] **Étape 2 : écrire le résolveur de contexte**

Créer `src/Atas.Strategy.TradeDeck/ContextResolver.cs` :

```csharp
using System;
using System.Collections.Generic;
using System.Linq;

using ATAS.DataFeedsCore;
using ATAS.Indicators;

namespace TradeDeck.Atas.Strategy;

/// <summary>
/// Turns the deck's selection — two strings, an account name and an instrument name — into the ATAS
/// objects an order needs.
///
/// RESOLVED AGAINST THE AGGREGATED HOST REGISTRY, NEVER AGAINST THE CHART, AND NEVER AGAINST A SINGLE
/// CONNECTOR. There is no such thing as "the" connector: portfolios are spread across whichever of
/// the platform's connectors are actually connected (measured live: 2 of 12, one of them a Rithmic
/// connector carrying a portfolio invisible from the chart). Every account/instrument/position/order
/// lookup below therefore walks <see cref="ConnectorRegistry.GetConnectors"/>, never a single
/// connector — and Order carries its own Portfolio and Security, so the deck can trade an instrument
/// the chart does not display.
///
/// DEGRADED MODE. When the registry is unreachable (<see cref="IsDegraded"/>), every lookup below
/// falls back to the chart's own <c>ITradingManager</c> — one account, one instrument, the chart's.
/// This is not a partial failure to patch around: it is the delivered fallback (see Contraintes
/// globales), entered and left silently except for the one transition log line in
/// <see cref="ConnectorRegistry.GetConnectors"/> (via <c>IsAvailable</c> flipping) — this class adds
/// no logging of its own for the transition, it only reads the registry's current answer on each
/// call.
/// </summary>
internal sealed class ContextResolver
{
    private readonly ConnectorRegistry _registry;
    private readonly Func<ITradingManager?> _tradingManager;

    public ContextResolver(ConnectorRegistry registry, Func<ITradingManager?> tradingManager)
    {
        _registry = registry;
        _tradingManager = tradingManager;
    }

    /// <summary>True once the host registry has proven unreachable — read for logging/diagnostics only.</summary>
    public bool IsDegraded => !_registry.IsAvailable;

    /// <summary>Every connector currently carrying a portfolio. Empty in degraded mode.</summary>
    public IReadOnlyList<IDataFeedConnector> Connectors => _registry.GetConnectors();

    public IDataFeedConnector? ConnectorFor(Portfolio? portfolio) => _registry.ConnectorFor(portfolio);

    public Portfolio? FindPortfolio(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;

        var connectors = _registry.GetConnectors();
        if (connectors.Count == 0) return DegradedPortfolio(name);

        foreach (var connector in connectors)
        {
            var portfolios = SafePortfolios(connector);
            foreach (var portfolio in portfolios)
            {
                if (string.Equals(portfolio?.AccountID, name, StringComparison.OrdinalIgnoreCase))
                    return portfolio;
            }
        }

        SdLog.Warn("Account not found: {0}", name);
        return null;
    }

    /// <summary>
    /// Chart-scoped fallback for FindPortfolio when the registry is unreachable. Only ONE portfolio
    /// exists in this mode — the chart's own, via TradingManager — so the lookup degrades to
    /// "is this the one account we have", rather than a search.
    /// </summary>
    private Portfolio? DegradedPortfolio(string name)
    {
        var portfolio = _tradingManager()?.Portfolio;
        if (portfolio == null) return null;
        if (string.Equals(portfolio.AccountID, name, StringComparison.OrdinalIgnoreCase)) return portfolio;

        SdLog.Warn("Account '{0}' not found — degraded mode only exposes the chart's own account ('{1}')",
            name, portfolio.AccountID ?? "?");
        return null;
    }

    /// <summary>
    /// Matches the deck's instrument string against what the registry knows, across every connector.
    ///
    /// Three passes, narrowest first, exact match always wins regardless of which connector or
    /// position in the enumeration it came from — see the ambiguity handling below. The deck sends
    /// whatever the trader typed on the Instrument key — "MNQ", "MNQ 09-26", or the broker's own
    /// symbol — and each broker names its contracts differently.
    /// </summary>
    public Security? FindSecurity(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;

        var connectors = _registry.GetConnectors();
        if (connectors.Count == 0) return DegradedSecurity(name);

        var wanted = name.Trim();
        Security? best = null;

        foreach (var connector in connectors)
        {
            var securities = SafeSecurities(connector);
            foreach (var security in securities)
            {
                if (security == null) continue;

                if (string.Equals(security.Code, wanted, StringComparison.OrdinalIgnoreCase)) return security;
                if (string.Equals(security.Instrument, wanted, StringComparison.OrdinalIgnoreCase)) return security;

                if (best == null && !string.IsNullOrEmpty(security.Code) &&
                    security.Code.StartsWith(wanted, StringComparison.OrdinalIgnoreCase))
                {
                    best = security;
                }
            }
        }

        if (best != null) return best;

        SdLog.Warn("Instrument not found: {0}", name);
        return null;
    }

    private Security? DegradedSecurity(string name)
    {
        var security = _tradingManager()?.Security;
        if (security == null) return null;
        if (string.Equals(security.Code, name, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(security.Instrument, name, StringComparison.OrdinalIgnoreCase))
            return security;

        SdLog.Warn("Instrument '{0}' not found — degraded mode only exposes the chart's own instrument ('{1}')",
            name, security.Code ?? "?");
        return null;
    }

    /// <summary>
    /// The open position, or null when flat.
    ///
    /// A position object with Volume == 0 is NOT a position: ATAS keeps the row after a close, and
    /// treating it as open would let flatten, break-even and reverse act on nothing while telling
    /// the deck they worked.
    /// </summary>
    public Position? FindPosition(Portfolio? portfolio, Security? security)
    {
        if (portfolio == null || security == null) return null;

        var connector = _registry.ConnectorFor(portfolio);
        if (connector == null) return IsDegraded ? DegradedPosition(portfolio, security) : null;

        foreach (var position in SafePositions(connector))
        {
            if (position == null) continue;
            if (!SamePortfolio(position.Portfolio, portfolio)) continue;
            if (!SameSecurity(position.Security, security)) continue;

            return position.Volume != 0 ? position : null;
        }

        return null;
    }

    private Position? DegradedPosition(Portfolio portfolio, Security security)
    {
        var tm = _tradingManager();
        var position = tm?.Position;
        if (position == null || position.Volume == 0m) return null;
        if (!SamePortfolio(position.Portfolio, portfolio) || !SameSecurity(position.Security, security)) return null;
        return position;
    }

    /// <summary>
    /// Every open position on this portfolio, across whichever connector owns it — used by
    /// flattenAccount, which liquidates the whole account rather than one instrument.
    /// </summary>
    public List<Position> FindAllPositions(Portfolio? portfolio)
    {
        var found = new List<Position>();
        if (portfolio == null) return found;

        var connector = _registry.ConnectorFor(portfolio);
        if (connector == null)
        {
            if (IsDegraded)
            {
                var position = _tradingManager()?.Position;
                if (position != null && position.Volume != 0m && SamePortfolio(position.Portfolio, portfolio))
                    found.Add(position);
            }
            return found;
        }

        foreach (var position in SafePositions(connector))
        {
            if (position == null || position.Volume == 0m) continue;
            if (!SamePortfolio(position.Portfolio, portfolio)) continue;
            found.Add(position);
        }
        return found;
    }

    /// <summary>
    /// Account names the deck may select, aggregated across every connected connector.
    ///
    /// Real accounts first, then simulated ones — the same order the NinjaTrader add-on publishes,
    /// so the Account key's list does not reshuffle when a trader moves between platforms.
    /// IsRealAccount is a FACT ATAS gives us, not a naming convention: the NinjaTrader side had to
    /// infer it from the provider, and the bridge still infers it from a "Sim" prefix.
    ///
    /// DEGRADED MODE returns AT MOST ONE NAME — the chart's own account. That is not a bug to fix:
    /// it is the entire point of the fallback, and it is what tells the trader, honestly, that the
    /// deck can no longer see the rest of the platform's accounts.
    /// </summary>
    public List<string> GetAccountNames()
    {
        var connectors = _registry.GetConnectors();
        if (connectors.Count == 0) return DegradedAccountNames();

        var live = new List<string>();
        var sim = new List<string>();

        foreach (var connector in connectors)
        {
            foreach (var portfolio in SafePortfolios(connector))
            {
                var name = portfolio?.AccountID;
                if (string.IsNullOrWhiteSpace(name)) continue;

                // A REPLAY PORTFOLIO IS NOT A TRADABLE ACCOUNT. Replay walks historical data and
                // produces real Order and MyTrade objects: offering it on the Account key would let
                // the trader place "orders" that go nowhere while the journal files fabricated
                // trades to Bitlearn as if they had happened. The NinjaTrader add-on guarded the
                // same hazard by excluding the accounts named Backtest and Playback101 — ATAS states
                // it as a fact instead of a name.
                if (SafeIsReplay(portfolio!)) continue;

                if (portfolio.IsRealAccount) live.Add(name); else sim.Add(name);
            }
        }

        return live.Concat(sim).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private List<string> DegradedAccountNames()
    {
        var portfolio = _tradingManager()?.Portfolio;
        if (portfolio == null || string.IsNullOrWhiteSpace(portfolio.AccountID)) return new List<string>();
        if (SafeIsReplay(portfolio)) return new List<string>();
        return new List<string> { portfolio.AccountID };
    }

    public List<Order> FindActiveOrders(Portfolio? portfolio, Security? security)
    {
        var found = new List<Order>();
        if (portfolio == null || security == null) return found;

        var connector = _registry.ConnectorFor(portfolio);
        if (connector == null)
        {
            if (IsDegraded) found.AddRange(DegradedOrders(portfolio, security, allInstruments: false));
            return found;
        }

        foreach (var order in SafeOrders(connector))
        {
            if (!IsActive(order)) continue;
            if (!SamePortfolio(order!.Portfolio, portfolio)) continue;
            if (!SameSecurity(order.Security, security)) continue;
            found.Add(order);
        }

        return found;
    }

    public List<Order> FindAllActiveOrders(Portfolio? portfolio)
    {
        var found = new List<Order>();
        if (portfolio == null) return found;

        var connector = _registry.ConnectorFor(portfolio);
        if (connector == null)
        {
            if (IsDegraded) found.AddRange(DegradedOrders(portfolio, security: null, allInstruments: true));
            return found;
        }

        foreach (var order in SafeOrders(connector))
        {
            if (!IsActive(order)) continue;
            if (!SamePortfolio(order!.Portfolio, portfolio)) continue;
            found.Add(order);
        }

        return found;
    }

    /// <summary>
    /// Degraded-mode order lookup, via TradingManager.Orders — confirmed live to show every order on
    /// the chart's account+instrument pair REGARDLESS OF ORIGIN, including a manual ChartTrader order
    /// (Comment empty). That is the discriminator GuardEnforcer relies on even in degraded mode.
    /// </summary>
    private IEnumerable<Order> DegradedOrders(Portfolio portfolio, Security? security, bool allInstruments)
    {
        var tm = _tradingManager();
        var orders = tm?.Orders;
        if (orders == null) yield break;

        foreach (var order in orders)
        {
            if (!IsActive(order)) continue;
            if (!SamePortfolio(order!.Portfolio, portfolio)) continue;
            if (!allInstruments && !SameSecurity(order.Security, security)) continue;
            yield return order;
        }
    }

    /// <summary>Working stop orders, sorted by trigger price ascending.</summary>
    public List<Order> FindStopOrders(Portfolio? portfolio, Security? security) =>
        FindActiveOrders(portfolio, security)
            .Where(o => o.Type is OrderTypes.Stop or OrderTypes.StopLimit)
            .OrderBy(o => o.TriggerPrice)
            .ToList();

    /// <summary>Working limit orders, sorted by price ascending.</summary>
    public List<Order> FindTargetOrders(Portfolio? portfolio, Security? security) =>
        FindActiveOrders(portfolio, security)
            .Where(o => o.Type == OrderTypes.Limit)
            .OrderBy(o => o.Price)
            .ToList();

    /// <summary>
    /// An order still worth acting on.
    ///
    /// Order.Status() rather than Order.State. The raw enum has only four values — None, Active,
    /// Done, Failed — where NinjaTrader had a dozen, and it cannot tell a filled order from a
    /// cancelled one. The Status() extension is what the platform's own order-management
    /// documentation uses, and it answers the question directly.
    ///
    /// PartlyFilled counts as live: the remainder is still working, and it is still a protection.
    /// </summary>
    public static bool IsActive(Order? order) =>
        order != null && order.Status() is OrderStatus.Placed or OrderStatus.PartlyFilled;

    /// <summary>
    /// The name this instrument goes by, everywhere: the state block, the journal, the logs.
    ///
    /// ONE function on purpose. The server groups a journal by this string and the deck matches its
    /// selection against it; two spellings of the same contract would split a day's trades in two.
    /// </summary>
    public static string InstrumentName(Security? security)
    {
        if (security == null) return string.Empty;
        if (!string.IsNullOrWhiteSpace(security.Code)) return security.Code;
        return security.Instrument ?? string.Empty;
    }

    /// <summary>
    /// Currency value of one full point — TickCost per TickSize.
    ///
    /// THE ONLY DERIVED FIELD OF THE WHOLE PORT, AND THE MOST SENSITIVE. NinjaTrader hands it over
    /// ready-made (MasterInstrument.PointValue); ATAS does not. The server computes every journal
    /// P&amp;L from this number and has no instrument table of its own, so a wrong value corrupts
    /// the statistics of every trade without anything ever looking broken.
    ///
    /// Returns 0 when it cannot be derived. 0 is honest: a caller can omit the field, whereas a
    /// fabricated 1.0 would silently price the whole day at a fiftieth of its value.
    /// </summary>
    public static decimal PointValue(Security? security)
    {
        if (security == null) return 0m;
        var tickSize = security.TickSize;
        if (tickSize <= 0m) return 0m;
        return security.TickCost / tickSize;
    }

    /// <summary>
    /// The price a deck command computes against, or 0 when there is no feed.
    ///
    /// Mid of best bid and ask, falling back to the last trade. 0 IS A REFUSAL SIGNAL, never a
    /// price: a limit computed from 0 lands a few ticks from zero, and a sell limit under the market
    /// fills instantly AT market. Every caller must test it — see NO_MARKET_DATA.
    /// </summary>
    public static decimal MarketPrice(Security? security)
    {
        if (security == null) return 0m;

        var bid = security.BestBidPrice;
        var ask = security.BestAskPrice;
        if (bid > 0m && ask > 0m) return (bid + ask) / 2m;
        if (ask > 0m) return ask;
        if (bid > 0m) return bid;

        var last = security.LastTradePrice ?? 0m;
        return last > 0m ? last : 0m;
    }

    public static bool SamePortfolio(Portfolio? a, Portfolio? b)
    {
        if (a == null || b == null) return false;
        if (ReferenceEquals(a, b)) return true;
        return !string.IsNullOrEmpty(a.AccountID) &&
               string.Equals(a.AccountID, b.AccountID, StringComparison.OrdinalIgnoreCase);
    }

    public static bool SameSecurity(Security? a, Security? b)
    {
        if (a == null || b == null) return false;
        if (ReferenceEquals(a, b)) return true;
        if (a.Id != 0 && a.Id == b.Id) return true;
        return !string.IsNullOrEmpty(a.SecurityId) &&
               string.Equals(a.SecurityId, b.SecurityId, StringComparison.OrdinalIgnoreCase);
    }

    // --- Defensive accessors -----------------------------------------------------------------
    //
    // Every lookup above walks collections owned by a connector this class does not control, on the
    // 500ms publish tick. A broker's connector rebuilding its portfolio list mid-walk, or throwing
    // out of a property it owns, must cost this ONE lookup, never the tick's real stateUpdate — same
    // reasoning as ConnectorRegistry.SafePortfolioCount.

    private static IEnumerable<Portfolio> SafePortfolios(IDataFeedConnector connector)
    {
        try { return connector.Portfolios ?? Enumerable.Empty<Portfolio>(); }
        catch (Exception ex) { SdLog.TraceEvent("Context", "Portfolios read faulted: {0}", ex.GetType().Name); return Enumerable.Empty<Portfolio>(); }
    }

    private static IEnumerable<Security> SafeSecurities(IDataFeedConnector connector)
    {
        try { return connector.Securities ?? Enumerable.Empty<Security>(); }
        catch (Exception ex) { SdLog.TraceEvent("Context", "Securities read faulted: {0}", ex.GetType().Name); return Enumerable.Empty<Security>(); }
    }

    private static IEnumerable<Position> SafePositions(IDataFeedConnector connector)
    {
        try { return connector.Positions ?? Enumerable.Empty<Position>(); }
        catch (Exception ex) { SdLog.TraceEvent("Context", "Positions read faulted: {0}", ex.GetType().Name); return Enumerable.Empty<Position>(); }
    }

    private static IEnumerable<Order> SafeOrders(IDataFeedConnector connector)
    {
        try { return connector.Orders ?? Enumerable.Empty<Order>(); }
        catch (Exception ex) { SdLog.TraceEvent("Context", "Orders read faulted: {0}", ex.GetType().Name); return Enumerable.Empty<Order>(); }
    }

    private static bool SafeIsReplay(Portfolio portfolio)
    {
        try { return portfolio.IsReplay(); }
        catch (Exception ex) { SdLog.TraceEvent("Context", "IsReplay() faulted: {0}", ex.GetType().Name); return false; }
    }
}
```

- [x] **Étape 3 : écrire le publieur d'état**

Créer `src/Atas.Strategy.TradeDeck/StatePublisher.cs` :

```csharp
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;

using ATAS.DataFeedsCore;
// ConnectionStates lives here, NOT in StockSharp.Messages (a same-named but unrelated type there —
// see the deviation note in the csproj) and not in ATAS.DataFeedsCore either.
using Utils.Common.Network.Client;

namespace TradeDeck.Atas.Strategy;

/// <summary>
/// Publishes the deck's view of the world every 500 ms, and on demand when something actually
/// moved.
///
/// EVERYTHING HERE IS TRACE. This loop runs twice a second: one INFO line in it produces hundreds
/// of thousands of lines a day, and the file that matters on a bad day becomes unopenable.
/// </summary>
internal sealed class StatePublisher : IDisposable
{
    private readonly ContextResolver _resolver;
    private readonly BridgeClient _bridge;
    private readonly int _intervalMs;

    private Timer? _timer;
    private bool _disposed;

    /// <summary>
    /// Guards against two publishes overlapping. The deck showing stale data is bad; two frames
    /// interleaved on one socket is worse — that is the send lock's problem, and this keeps it from
    /// ever being tested.
    /// </summary>
    private int _publishing;

    private int _consecutiveSkips;

    private readonly object _trackingLock = new();
    private string _account = string.Empty;
    private string _instrument = string.Empty;

    public string TrackedAccount { get { lock (_trackingLock) return _account; } }
    public string TrackedInstrument { get { lock (_trackingLock) return _instrument; } }

    public StatePublisher(ContextResolver resolver, BridgeClient bridge, int intervalMs)
    {
        _resolver = resolver;
        _bridge = bridge;
        _intervalMs = intervalMs;
    }

    public void Start(string account, string instrument)
    {
        lock (_trackingLock)
        {
            _account = account ?? string.Empty;
            _instrument = instrument ?? string.Empty;
        }

        _timer = new Timer(_ => Publish(false), null, 0, _intervalMs);
        SdLog.Event("State", "State publishing started every {0}ms — account='{1}' instrument='{2}'",
            _intervalMs, account, instrument);
    }

    public void UpdateTracking(string? account, string? instrument)
    {
        lock (_trackingLock)
        {
            if (!string.IsNullOrWhiteSpace(account)) _account = account!;
            if (!string.IsNullOrWhiteSpace(instrument)) _instrument = instrument!;
        }

        SdLog.Event("State", "Tracking changed — account='{0}' instrument='{1}'", TrackedAccount, TrackedInstrument);
        PublishNow();
    }

    /// <summary>Refreshes the deck on the spot instead of waiting for the next tick.</summary>
    public void PublishNow()
    {
        if (_disposed) return;
        try { Publish(true); }
        catch (Exception ex) { SdLog.Fail("StatePublish", ex, "Immediate state publish failed"); }
    }

    private void Publish(bool immediate)
    {
        if (_disposed || !_bridge.IsConnected) return;

        if (Interlocked.CompareExchange(ref _publishing, 1, 0) != 0)
        {
            // An immediate publish losing the race is not a stall: the publish already in flight is
            // milliseconds old and the timer republishes right after. Counting it would make the
            // stall detector fire on healthy bursts of fills.
            if (immediate)
            {
                SdLog.TraceEvent("StatePublish", "Immediate publish skipped — a publish is already in flight");
                return;
            }

            _consecutiveSkips++;
            // The first skip is routine; a sustained run means the deck is frozen on stale data,
            // which the trader reads as "the buttons stopped updating".
            if (_consecutiveSkips == 5 || _consecutiveSkips % 50 == 0)
            {
                SdLog.EventWarn("StatePublish",
                    "State publish stalled — {0} consecutive ticks skipped ({1}ms interval); the deck is showing stale data",
                    _consecutiveSkips, _intervalMs);
            }
            else
            {
                SdLog.TraceEvent("StatePublish", "Tick skipped — previous publish still in flight");
            }
            return;
        }

        if (_consecutiveSkips > 0)
        {
            SdLog.Event("StatePublish", "State publishing resumed after {0} skipped tick(s)", _consecutiveSkips);
            _consecutiveSkips = 0;
        }

        var sendStarted = false;
        try
        {
            var accounts = _resolver.GetAccountNames();
            AdoptDefaultAccountIfNeeded(accounts);

            var portfolio = _resolver.FindPortfolio(TrackedAccount);
            var security = _resolver.FindSecurity(TrackedInstrument);

            var state = BuildState(portfolio, security, accounts);
            var task = _bridge.SendAsync(BridgeMessage.Notify("stateUpdate", state));
            sendStarted = true;
            task.ContinueWith(_ => Interlocked.Exchange(ref _publishing, 0));
        }
        catch (Exception ex)
        {
            SdLog.Fail("StatePublish", ex, "Failed to publish state for {0} / {1}", TrackedAccount, TrackedInstrument);
        }
        finally
        {
            if (!sendStarted) Interlocked.Exchange(ref _publishing, 0);
        }
    }

    /// <summary>
    /// Falls back to the first connected account when the tracked one is gone.
    ///
    /// A default, never a choice. The bridge pushes setAccount on every connection precisely so this
    /// path is not the one that decides: on 2026-08-21 an unpersisted account left the NinjaTrader
    /// add-on enforcing on an account nobody was trading, while the deck showed the macro armed.
    /// </summary>
    private void AdoptDefaultAccountIfNeeded(List<string> accounts)
    {
        lock (_trackingLock)
        {
            if (accounts.Count == 0)
            {
                if (_account.Length == 0) return;
                _account = string.Empty;
                SdLog.Event("State", "Tracked account cleared because no portfolio is connected.");
                return;
            }

            if (_account.Length > 0 && accounts.Any(a => string.Equals(a, _account, StringComparison.OrdinalIgnoreCase)))
                return;

            _account = accounts[0];
            SdLog.EventWarn("State",
                "Account auto-selected from the connected list: {0} — nobody chose it; the bridge should push setAccount",
                _account);
        }
    }

    private Dictionary<string, object?> BuildState(Portfolio? portfolio, Security? security, List<string> accounts)
    {
        var account = new Dictionary<string, object?>
        {
            ["name"] = portfolio?.AccountID ?? TrackedAccount,
            ["connected"] = portfolio != null && portfolio.ConnectionState == ConnectionStates.Connected,
        };

        AddAccountPnl(account, portfolio);
        AddAccountBalance(account, portfolio);

        Dictionary<string, object?>? instrument = null;
        Dictionary<string, object?>? position = null;

        if (security != null)
        {
            var pointValue = ContextResolver.PointValue(security);
            instrument = new Dictionary<string, object?>
            {
                ["name"] = ContextResolver.InstrumentName(security),
                ["lastPrice"] = ContextResolver.MarketPrice(security),
                ["tickSize"] = security.TickSize,
                // 1.0 rather than 0 for the DISPLAY value only, matching the NinjaTrader add-on: the
                // deck divides by it to show a P&L per point. The journal's own pointValue is a
                // different field and is omitted rather than faked — see ExecutionRecorder.
                ["pointValue"] = pointValue > 0m ? pointValue : 1.0m,
            };

            if (portfolio != null) position = BuildPosition(portfolio, security);
        }

        return new Dictionary<string, object?>
        {
            ["connected"] = true,
            ["account"] = account,
            ["instrument"] = instrument,
            ["position"] = position,
            ["availableAccounts"] = accounts,
            // No `trend` block and no `copier` block: both are out of scope. The protocol says an
            // absent trend leaves the bridge's last value in place and the key shows NO DATA, which
            // is exactly true here.
        };
    }

    private Dictionary<string, object?> BuildPosition(Portfolio portfolio, Security security)
    {
        var position = _resolver.FindPosition(portfolio, security);
        var stops = _resolver.FindStopOrders(portfolio, security);
        var targets = _resolver.FindTargetOrders(portfolio, security);
        var active = _resolver.FindActiveOrders(portfolio, security);

        if (position == null)
        {
            // EVERY FIELD IS PRESENT ON BOTH BRANCHES. The protocol declares them required, and
            // omitting the counts made the protection signature flip between "-" and a number on
            // every open and close, logging a transition that had not happened.
            return new Dictionary<string, object?>
            {
                ["exists"] = false,
                ["direction"] = "Flat",
                ["quantity"] = 0,
                ["averagePrice"] = 0m,
                ["unrealizedPnl"] = 0m,
                ["hasStopOrder"] = false,
                ["stopPrice"] = 0m,
                ["stopOrderCount"] = 0,
                ["hasTargetOrder"] = false,
                ["targetPrice"] = 0m,
                ["targetOrderCount"] = 0,
                ["activeOrderCount"] = active.Count,
            };
        }

        var isLong = position.Volume > 0m;

        // The stop that actually protects the position is the TIGHTEST one — the highest under a
        // long, the lowest over a short — not whichever the connector listed first. Showing an
        // arbitrary one tells the trader he has more room than he has.
        var tightestStop = stops.Count == 0 ? null : isLong ? stops[^1] : stops[0];
        var nearestTarget = targets.Count == 0 ? null : isLong ? targets[0] : targets[^1];

        return new Dictionary<string, object?>
        {
            ["exists"] = true,
            ["direction"] = isLong ? "Long" : "Short",
            ["quantity"] = (int)Math.Abs(position.Volume),
            ["averagePrice"] = position.AveragePrice,
            ["unrealizedPnl"] = position.UnrealizedPnL,
            ["hasStopOrder"] = stops.Count > 0,
            ["stopPrice"] = tightestStop?.TriggerPrice ?? 0m,
            ["stopOrderCount"] = stops.Count,
            ["hasTargetOrder"] = targets.Count > 0,
            ["targetPrice"] = nearestTarget?.Price ?? 0m,
            ["targetOrderCount"] = targets.Count,
            ["activeOrderCount"] = active.Count,
        };
    }

    /// <summary>
    /// Account P&amp;L, which the bridge's safety macro measures the daily loss from.
    ///
    /// ClosedPnL, NEVER TotalClosedPnL. The second one accumulates across sessions; the first
    /// carries a date (ClosedPnlDate). The daily loss limit is measured "since the start-of-day
    /// P&amp;L", so feeding it a lifetime figure would trip the cap at the open on any account with
    /// a losing history, and the macro would then refuse everything, for ever.
    /// </summary>
    private static void AddAccountPnl(Dictionary<string, object?> account, Portfolio? portfolio)
    {
        if (portfolio == null)
        {
            account["realizedPnl"] = 0m;
            account["unrealizedPnl"] = 0m;
            // pnlAvailable=false tells the bridge the P&L rules have no data to work with, which
            // surfaces on the deck instead of failing silently.
            account["pnlAvailable"] = false;
            return;
        }

        account["realizedPnl"] = portfolio.ClosedPnL;
        account["unrealizedPnl"] = portfolio.OpenPnL;
        account["pnlAvailable"] = true;
    }

    /// <summary>
    /// Account cash value, forwarded so the Bitlearn journal can start from the real capital.
    ///
    /// Without it the journal opens at zero and every percentage it computes is meaningless — a
    /// gain of 200 on nothing is not a percentage. Kept separate from the P&amp;L because it fails
    /// independently: losing the balance must never cost the P&amp;L the loss limits depend on.
    ///
    /// The field is OMITTED rather than sent as 0 when no source has a usable value. The bridge only
    /// overwrites its cash value when the field is a number, so an omission leaves the journal on
    /// the last real capital it knew instead of resetting it to nothing.
    /// </summary>
    private static void AddAccountBalance(Dictionary<string, object?> account, Portfolio? portfolio)
    {
        if (portfolio == null) return;

        if (portfolio.Balance != 0m) { account["cashValue"] = portfolio.Balance; return; }
        if (portfolio.BalanceAvailable is { } available && available != 0m) { account["cashValue"] = available; return; }

        SdLog.TraceEvent("Account", "No usable cash value for {0} — the Bitlearn journal keeps its current capital",
            portfolio.AccountID ?? "?");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer?.Dispose();
        _timer = null;
    }
}
```

- [x] **Étape 4 : brancher la publication et les commandes de suivi**

> ⏸ Redémarrée avec les étapes 2 et 3 : `new ContextResolver(() => Connector)` construisait le
> résolveur contre `ChartStrategy.Connector`, toujours nul. Il se construit désormais contre le
> `ConnectorRegistry` et le `TradingManager` — voir ci-dessous.

Dans `src/Atas.Strategy.TradeDeck/TradeDeckStrategy.cs`, ajouter les champs sous `_bridge` :

```csharp
    private ConnectorRegistry? _registry;
    private ContextResolver? _resolver;
    private StatePublisher? _publisher;

    private const int StateIntervalMs = 500;

    /// <summary>
    /// True once the bridge has named the account to track.
    ///
    /// Until then we are on whatever account the connector listed first, chosen by nobody. Every
    /// safety rule scoped to an account rides on that choice — which is how, on 2026-08-21, a locked
    /// macro coexisted with an hour of hand-placed orders on the NinjaTrader side. The bridge pushes
    /// the account on every connection; this flag is the tripwire that says so out loud when it
    /// does not.
    /// </summary>
    private volatile bool _accountConfirmedByBridge;
```

Dans `OnStarted()`, après `_bridge.Start();` :

```csharp
            // ChartStrategy.Connector is confirmed always null on classic ATAS — never used here.
            // The registry is the primary path; ContextResolver degrades to the protected
            // Indicator.TradingManager getter (reachable only from this class, passed down as a
            // delegate) when the registry cannot be reached.
            _registry = new ConnectorRegistry();
            _resolver = new ContextResolver(_registry, () => TradingManager);
            _publisher = new StatePublisher(_resolver, _bridge, StateIntervalMs);
            // Empty instrument on purpose: a hardcoded contract goes stale and would make the deck
            // show — and trade — something the trader never picked. The bridge replays its
            // selection on every connection.
            _publisher.Start(_resolver.GetAccountNames().FirstOrDefault() ?? string.Empty, string.Empty);
```

(ajouter `using ATAS.Indicators;` en tête de fichier — c'est là que vit `ITradingManager`.)

(ajouter `using System.Linq;` en tête de fichier.)

Dans `OnStopping()`, **avant** l'arrêt du client :

```csharp
        // Before the bridge client, which it publishes through: stopping the socket first would
        // leave a publish in flight writing to a disposed client.
        try { _publisher?.Dispose(); }
        catch (Exception ex) { SdLog.Fail("Session", ex, "Error while stopping the state publisher"); }
```

et dans `OnStopped()`, après la libération du client : `_publisher = null; _resolver = null; _registry = null;`.

Remplacer enfin le corps de `OnBridgeMessage` après la ligne de journalisation :

```csharp
        var startedAt = DateTime.UtcNow;

        var response = message.Action switch
        {
            "setAccount" or "setInstrument" => HandleTracking(message),
            // Out of scope, and answered honestly rather than with a comfortable lie. The bridge
            // FORCES both on every add-on connection; replying "success" would show the trader a
            // copier that mirrors nothing and a trend rule that judges nothing.
            "setCopierConfig" or "copierPanic" or "configureTrend" => Unsupported(message),
            _ => BridgeMessage.Failure(message.RequestId, message.Action,
                     "UNSUPPORTED_ACTION", $"Action '{message.Action}' is not supported yet by the ATAS strategy."),
        };

        LogOutcome(message, response, startedAt);
        _ = _bridge?.SendAsync(response);
```

et ajouter les trois méthodes :

```csharp
    private BridgeMessage HandleTracking(BridgeMessage message)
    {
        if (_publisher == null)
            return BridgeMessage.Failure(message.RequestId, message.Action, "CONTEXT_MISSING", "State publisher is not initialized.");

        var account = message.GetString("account");
        var instrument = message.GetString("instrument");

        if (message.Action == "setAccount" && string.IsNullOrWhiteSpace(account))
            return BridgeMessage.Failure(message.RequestId, message.Action, "CONTEXT_MISSING", "Account name is required.");

        if (message.Action == "setInstrument" && string.IsNullOrWhiteSpace(instrument))
            return BridgeMessage.Failure(message.RequestId, message.Action, "CONTEXT_MISSING", "Instrument name is required.");

        // The account now comes from the bridge, not from this side's startup guess.
        if (!string.IsNullOrWhiteSpace(account)) _accountConfirmedByBridge = true;

        _publisher.UpdateTracking(account, instrument);

        return BridgeMessage.Response(message.RequestId, message.Action, true, new
        {
            account = _publisher.TrackedAccount,
            instrument = _publisher.TrackedInstrument,
        });
    }

    private static readonly HashSet<string> UnsupportedReported = new(StringComparer.Ordinal);

    /// <summary>
    /// Refuses an out-of-scope action, and says so ONCE per action.
    ///
    /// The bridge forces setCopierConfig on every connection and the host replays configureTrend on
    /// every layout edit: warning on each one would train the reader to skip the category on the day
    /// it carries something else.
    /// </summary>
    private static BridgeMessage Unsupported(BridgeMessage message)
    {
        lock (UnsupportedReported)
        {
            if (UnsupportedReported.Add(message.Action))
            {
                SdLog.EventWarn("Command",
                    "'{0}' is out of scope for the ATAS port (trend macro and account copier ship later) — refused, and this is the only warning",
                    message.Action);
            }
        }

        return BridgeMessage.Failure(message.RequestId, message.Action, "UNSUPPORTED_ACTION",
            $"'{message.Action}' is not ported to ATAS yet.");
    }

    /// <summary>
    /// Records what the strategy answered and how long it took. The duration matters: a command
    /// that normally answers in a few ms and suddenly takes seconds points at the platform — data
    /// feed, account lock — rather than at the deck.
    /// </summary>
    private static void LogOutcome(BridgeMessage request, BridgeMessage response, DateTime startedAt)
    {
        var elapsedMs = (int)(DateTime.UtcNow - startedAt).TotalMilliseconds;

        if (response.Error != null)
        {
            SdLog.EventWarn("Command", "[REQ:{0}] {1} refused: {2} — {3} ({4}ms)",
                request.RequestId ?? "n/a", request.Action, response.Error.Code, response.Error.Message, elapsedMs);
            return;
        }

        SdLog.Event("Command", "[REQ:{0}] {1} completed ({2}ms)", request.RequestId ?? "n/a", request.Action, elapsedMs);
    }
```

(ajouter `using System.Collections.Generic;` en tête de fichier.)

- [x] **Étape 5 : compiler**

> ⏸ Redémarrée avec les étapes 1 à 4 ci-dessus : le build réussi précédemment portait sur la
> conception invalidée (résolution contre `ChartStrategy.Connector`).

```bash
dotnet build src/Atas.Strategy.TradeDeck/Atas.Strategy.TradeDeck.csproj -c Release
```

Attendu : `Build succeeded`.

- [ ] **Étape 6 : vérifier le registre puis la publication — recette étapes 0 et 2**

> ⏸ Non exécutée par ce protocole écrit (exige ATAS classique ouvert, un compte de simulation et le
> bridge lancé), mais **déjà établie sur le terrain le 24/08** par une sonde d'investigation
> (non commitée, voir `progress.md`, section « SONDE SERVICELOCATOR ») : la chaîne de réflexion
> fonctionne bel et bien sur cette machine. Cette étape rejoue la même preuve à travers le code
> propre livré par les étapes 1 à 4, plutôt que la sonde jetable.

**Étape 0 de la recette — le registre répond.** Avant même de regarder `stateUpdate`, vérifier que
`ConnectorRegistry` a bien résolu la chaîne d'hôte :

```powershell
Get-Content "$env:APPDATA\StreamDeckTrader\logs\addon-$(Get-Date -f yyyy-MM-dd).log" -Tail 60
```

Attendu, l'une des deux lignes (transition unique, loggée une fois — ne pas s'attendre à la revoir à
chaque tick) :
- `Registry | Connector registry reachable — resolving deck accounts against IConnectorsManager.Connectors`
  — le chemin normal, **ce qu'on veut voir** ; ou
- `Registry | Connector registry unreachable (…) — degrading to the chart's own account and instrument via TradingManager`
  — le mode dégradé s'est enclenché. C'est **le basculement qu'on vérifie alors, pas la suite** :
  confirmer que le boîtier affiche quand même **un** compte et **un** instrument (ceux du graphique)
  plutôt que rien, avant de continuer.

Si aucune des deux lignes n'apparaît, la stratégie n'a pas atteint son premier tick de publication —
revenir à l'étape 4 de la tâche 2 (le bridge la voit-il comme add-on connecté ?) avant de creuser ici.

**Étape 2 de la recette — la publication.** Déployer le DLL (`%APPDATA%\ATAS\Strategies\`,
**redémarrer ATAS** — voir la tâche 2), stratégie démarrée sur un graphique avec portefeuille et flux
connectés, bridge lancé, hôte TradeDeck lancé, puis :

```powershell
Get-Content "$env:APPDATA\StreamDeckTrader\logs\bridge-$(Get-Date -f yyyy-MM-dd).log" -Tail 40
```

Attendu :
- le journal du bridge montre des trames `AddOn → Bridge` contenant `"stateUpdate"` **toutes les
  500 ms environ** (elles sont en `Trace` : lancer le bridge avec
  `$env:Logging__LogLevel__Default = "Trace"` si le fichier n'en montre aucune) ;
- le boîtier affiche le **compte sélectionné** et l'**instrument sélectionné**, `FLAT`, et un P&L de
  compte cohérent avec ce qu'ATAS affiche ;
- appuyer sur la touche Compte pour changer de compte : le nouveau nom apparaît sur la touche **et**
  dans `addon-*.log` en `State | Tracking changed — account='…'` — **si le registre est joignable**,
  la liste doit porter tous les comptes connectés, pas seulement celui du graphique ; en mode dégradé
  elle n'en porte qu'un, ce qui est le comportement attendu et pas un bug ;
- **contrôle du piège `ClosedPnL`** : comparer `realizedPnl` publié avec le P&L **du jour** affiché
  par ATAS pour ce portefeuille. S'il correspond au cumul de toutes les séances, c'est
  `TotalClosedPnL` qui a été lu — corriger avant de continuer, sinon la macro refusera tout dès
  l'ouverture sur un compte en perte historique.

Rappel du piège classique (voir tâche 2, étape 8) : un `stateUpdate` qui s'arrête net est d'abord le
signe qu'ATAS a arrêté la stratégie en silence (changement d'instrument, d'unité de temps ou de
portefeuille sur le graphique, rechargement d'historique, fermeture du graphique, redépôt du DLL) —
vérifier l'état de la stratégie dans ATAS avant de chercher un bug dans ce code.

- [x] **Étape 7 : committer**

```bash
git add src/Atas.Strategy.TradeDeck/ binaries/
git commit -m "Publier l etat du compte, de l instrument et de la position depuis ATAS"
```

---

## Tâche 4 : ordres au marché, aiguillage et remontée des rejets

**C'est le vrai jalon du portage.** Les étapes 3 et 4 de la recette prouvent d'un coup le transport,
la sérialisation, la résolution compte/instrument et la remontée des rejets. Tout le reste est
variation sur une plomberie prouvée — c'est pourquoi elles se font avant les douze autres commandes
et non après.

**Fichiers :**
- Créer : `src/Atas.Strategy.TradeDeck/OrderEngine.cs`
- Créer : `src/Atas.Strategy.TradeDeck/CommandDispatcher.cs`
- Créer : `src/Atas.Strategy.TradeDeck/OrderMonitor.cs`
- Modifier : `src/Atas.Strategy.TradeDeck/TradeDeckStrategy.cs`

**Interfaces :**
- Consomme : `ContextResolver`, `StatePublisher`, `BridgeClient`, `BridgeMessage` (tâches 2 et 3).
- Produit : `IOrderGateway` (**publique** — une classe publique ne peut pas implémenter une
  interface interne) — `void Open(Order)`, `void Cancel(Order)`,
  `void Modify(Order oldOrder, Order newOrder)`, `bool IsStopLossOrder(Order)`,
  `bool IsTakeProfitOrder(Order)`. Implémentée par `TradeDeckStrategy`.
- Produit : `OrderEngine` —
  `OrderEngine(ContextResolver resolver, IOrderGateway gateway)`,
  `BridgeMessage BuyMarket(BridgeMessage)`, `BridgeMessage SellMarket(BridgeMessage)`,
  `bool IsOurs(Order?)`, `const string DeckOrderComment = "StreamDeck"`.
  **Et les primitives que les tâches 5 à 7 réutilisent, écrites ici une fois pour toutes :**
  `readonly struct TradingContext { Portfolio? Portfolio; Security? Security; BridgeMessage? Error; }`,
  `TradingContext Resolve(BridgeMessage)`,
  `Order NewOrder(TradingContext, OrderDirections, OrderTypes, decimal quantity, string comment)`,
  `BridgeMessage? Submit(BridgeMessage, Order)`,
  `BridgeMessage? Cancel(BridgeMessage, IReadOnlyList<Order>)`,
  `BridgeMessage? Modify(BridgeMessage, Order, Action<Order> reprice)`,
  `static decimal RoundToTick(decimal price, decimal tickSize)`.
  Les dix commandes restantes arrivent aux tâches 5 à 7.
- Produit : `CommandDispatcher` — `CommandDispatcher(OrderEngine)`,
  `static bool Handles(string action)`, `BridgeMessage Dispatch(BridgeMessage)`.
- Produit : `OrderMonitor` —
  `OrderMonitor(BridgeClient)`, `void Refresh(IReadOnlyList<IDataFeedConnector> connectors)`,
  `void DetachAll()`, `Action? StateChanged { get; set; }`, `void Dispose()`.
  **`Refresh`, pas `Attach(IDataFeedConnector)` : un seul connecteur n'existe pas.** Les portefeuilles
  sont répartis sur plusieurs connecteurs (tâche 3) ; `Refresh` reconcilie l'ensemble des abonnements
  contre `ContextResolver.Connectors` à chaque tick de publication — un connecteur peut se
  (dé)connecter en cours de session, comme le Rithmic du 24/08 qui portait un portefeuille invisible
  du graphique. Les deux paramètres facultatifs du constructeur arrivent aux tâches 8 et 9.

- [x] **Étape 1 : écrire le moteur d'ordres**

Créer `src/Atas.Strategy.TradeDeck/OrderEngine.cs` :

```csharp
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

using ATAS.DataFeedsCore;
using ATAS.Indicators;

namespace TradeDeck.Atas.Strategy;

/// <summary>
/// The three order verbs of ChartStrategy, plus the two classifications only ITradingManager can
/// make, behind one seam.
///
/// An interface rather than four delegates because these five members always come from the same
/// place — the running strategy — and because it keeps OrderEngine, GuardEnforcer and
/// ExecutionRecorder from each having to know how to reach it.
/// </summary>
public interface IOrderGateway
{
    /// <summary>ChartStrategy.OpenOrder — sends the order, never confirms it was accepted.</summary>
    void Open(Order order);

    void Cancel(Order order);

    /// <summary>ChartStrategy.ModifyOrder — the SECOND argument is the repriced clone.</summary>
    void Modify(Order oldOrder, Order newOrder);

    bool IsStopLossOrder(Order order);
    bool IsTakeProfitOrder(Order order);
}

/// <summary>
/// Builds and submits the orders the deck asks for.
///
/// THE DECK DECIDES, NOT THE CHART. Portfolio and Security are set explicitly on every order from
/// what the deck selected. ITradingManager's own SetStopLoss / SetTakeProfit / SetBreakeven are
/// deliberately NOT used, despite the code they would save: they act on the CHART's selection, so
/// the moment the deck points at an instrument the chart does not display they would send the
/// break-even to the wrong position.
/// </summary>
internal sealed class OrderEngine
{
    /// <summary>
    /// The comment every order this deck submits carries. ATAS's Order has no Name — this is the
    /// closest equivalent, and it keeps the same vocabulary the server already reads in the
    /// journal ("StreamDeck", "StreamDeck_SL", …).
    /// </summary>
    public const string DeckOrderComment = "StreamDeck";

    private readonly ContextResolver _resolver;
    private readonly IOrderGateway _gateway;

    /// <summary>
    /// Orders this engine created.
    ///
    /// TRACKED BY OBJECT, NOT BY COMMENT. A comment is a free string a trader can type into a DOM
    /// ticket, so trusting it alone would hand anyone a one-word bypass of every rule GuardEnforcer
    /// applies. The comment stays — the server reads it in the journal, and it survives ATAS handing
    /// a different instance back — but the table below is what the guard trusts first.
    ///
    /// A ConditionalWeakTable so a forgotten order cannot leak: entries vanish with the order.
    /// </summary>
    private readonly ConditionalWeakTable<Order, object> _ours = new();

    public OrderEngine(ContextResolver resolver, IOrderGateway gateway)
    {
        _resolver = resolver;
        _gateway = gateway;
    }

    public bool IsOurs(Order? order)
    {
        if (order == null) return false;
        if (_ours.TryGetValue(order, out _)) return true;

        var comment = order.Comment;
        return !string.IsNullOrEmpty(comment) &&
               comment.StartsWith(DeckOrderComment, StringComparison.OrdinalIgnoreCase);
    }

    public BridgeMessage BuyMarket(BridgeMessage cmd) => SubmitMarket(cmd, OrderDirections.Buy);
    public BridgeMessage SellMarket(BridgeMessage cmd) => SubmitMarket(cmd, OrderDirections.Sell);

    private BridgeMessage SubmitMarket(BridgeMessage cmd, OrderDirections direction)
    {
        var ctx = Resolve(cmd);
        if (ctx.Error != null) return ctx.Error;

        var quantity = cmd.GetInt("quantity") ?? 1;
        if (quantity < 1)
            return BridgeMessage.Failure(cmd.RequestId, cmd.Action, "INVALID_QUANTITY", "Quantity must be >= 1");

        var order = NewOrder(ctx, direction, OrderTypes.Market, quantity, DeckOrderComment);

        var failure = Submit(cmd, order);
        if (failure != null) return failure;

        SdLog.Event("Order", "[REQ:{0}] {1} {2} {3} Market submitted on {4}",
            cmd.RequestId, direction, quantity, ContextResolver.InstrumentName(ctx.Security), ctx.Portfolio!.AccountID);

        // Submission is asynchronous, exactly as Account.Submit was: a rejection (margin, closed
        // market) arrives later through OrderMonitor. This reply says the order LEFT, never that it
        // was accepted.
        return BridgeMessage.Response(cmd.RequestId, cmd.Action, true, new
        {
            orderId = order.Id ?? string.Empty,
            message = $"{direction} {quantity} {ContextResolver.InstrumentName(ctx.Security)} Market submitted",
        });
    }

    /// <summary>
    /// Builds an order carrying the DECK's portfolio and security, never the chart's.
    /// </summary>
    public Order NewOrder(TradingContext ctx, OrderDirections direction, OrderTypes type,
                          decimal quantity, string comment)
    {
        var order = new Order
        {
            Portfolio = ctx.Portfolio,
            Security = ctx.Security,
            Direction = direction,
            Type = type,
            QuantityToFill = quantity,
            Comment = comment,
            // Day, like the NinjaTrader add-on: a forgotten GTC entry could fill unattended in a
            // later session, on a market the trader is not watching.
            TimeInForce = TimeInForce.Day,
        };

        _ours.Add(order, this);
        return order;
    }

    /// <summary>
    /// Submits, or returns the refusal to send back to the deck.
    ///
    /// Goes through ChartStrategy.OpenOrder — the documented strategy API, which takes the order and
    /// nothing else. ITradingManager also carries an OpenOrder with four booleans
    /// (setDefaultQuantity, askConfirmation, checkOrderStates) and the design brief reached for it;
    /// it is NOT what the official documentation nor the SmaChartStrategy example use, and taking
    /// the documented path means there is no confirmation dialog to reason about at all. A modal
    /// window opening in the middle of a key press would turn the cockpit into a trap.
    ///
    /// "The OpenOrder method does not guarantee that an order will be placed, it only sends the
    /// order to the exchange" — the documentation's own words, and the reason OrderMonitor exists.
    /// </summary>
    public BridgeMessage? Submit(BridgeMessage cmd, Order order)
    {
        try
        {
            _gateway.Open(order);
            return null;
        }
        catch (Exception ex)
        {
            SdLog.Fail("Order", ex, "[REQ:{0}] Failed to submit {1} {2}",
                cmd.RequestId, order.Direction, order.Type);
            return BridgeMessage.Failure(cmd.RequestId, cmd.Action, "ORDER_REJECTED", ex.Message);
        }
    }

    public BridgeMessage? Cancel(BridgeMessage cmd, IReadOnlyList<Order> orders)
    {
        try
        {
            foreach (var order in orders)
            {
                // The documentation asks for this check before cancelling: an order that already
                // reached a terminal state only produces a "cancel rejected" that means nothing.
                if (!ContextResolver.IsActive(order)) continue;
                _gateway.Cancel(order);
            }
            return null;
        }
        catch (Exception ex)
        {
            SdLog.Fail("Order", ex, "[REQ:{0}] Failed to cancel {1} order(s)", cmd.RequestId, orders.Count);
            return BridgeMessage.Failure(cmd.RequestId, cmd.Action, "ORDER_REJECTED", ex.Message);
        }
    }

    /// <summary>
    /// Replaces an order with a repriced clone.
    ///
    /// ATAS modifies by handing over a NEW order, where NinjaTrader mutated the existing one and
    /// called Account.Change. Cloning keeps every field we are not changing — quantity, OCO group,
    /// time in force — instead of rebuilding them and getting one wrong.
    /// </summary>
    public BridgeMessage? Modify(BridgeMessage cmd, Order order, Action<Order> reprice)
    {
        try
        {
            var updated = order.Clone();
            reprice(updated);
            _ours.AddOrUpdate(updated, this);
            _gateway.Modify(order, updated);
            return null;
        }
        catch (Exception ex)
        {
            SdLog.Fail("Order", ex, "[REQ:{0}] Failed to modify order {1}", cmd.RequestId, order.Id ?? "?");
            return BridgeMessage.Failure(cmd.RequestId, cmd.Action, "ORDER_REJECTED", ex.Message);
        }
    }

    /// <summary>Rounds to a whole number of ticks — a price off the grid is refused by the venue.</summary>
    public static decimal RoundToTick(decimal price, decimal tickSize) =>
        tickSize <= 0m ? price : Math.Round(price / tickSize, MidpointRounding.AwayFromZero) * tickSize;

    public readonly struct TradingContext
    {
        public Portfolio? Portfolio { get; init; }
        public Security? Security { get; init; }
        public BridgeMessage? Error { get; init; }
    }

    public TradingContext Resolve(BridgeMessage cmd)
    {
        var accountName = cmd.GetString("account");
        var instrumentName = cmd.GetString("instrument");

        if (string.IsNullOrWhiteSpace(accountName))
            return Refuse(cmd, "CONTEXT_MISSING", "Account name is required.");

        if (string.IsNullOrWhiteSpace(instrumentName))
            return Refuse(cmd, "CONTEXT_MISSING", "Instrument name is required.");

        var portfolio = _resolver.FindPortfolio(accountName);
        if (portfolio == null)
            return Refuse(cmd, "ACCOUNT_NOT_FOUND", $"Account '{accountName}' not found.");

        var security = _resolver.FindSecurity(instrumentName);
        if (security == null)
            return Refuse(cmd, "INSTRUMENT_NOT_FOUND", $"Instrument '{instrumentName}' not found.");

        return new TradingContext { Portfolio = portfolio, Security = security };
    }

    private static TradingContext Refuse(BridgeMessage cmd, string code, string message) =>
        new() { Error = BridgeMessage.Failure(cmd.RequestId, cmd.Action, code, message) };
}
```

- [x] **Étape 2 : écrire l'aiguilleur**

Créer `src/Atas.Strategy.TradeDeck/CommandDispatcher.cs`. Les onze branches encore absentes sont
laissées en `UNSUPPORTED_ACTION` et remplies aux tâches 5 à 7 — chacune est nommée dès maintenant
pour que la liste des treize soit lisible d'un coup d'œil.

```csharp
using System;

namespace TradeDeck.Atas.Strategy;

/// <summary>Routes a deck command to the engine method that performs it.</summary>
internal sealed class CommandDispatcher
{
    private readonly OrderEngine _engine;

    public CommandDispatcher(OrderEngine engine) => _engine = engine;

    /// <summary>True when this action is one of the thirteen the engine performs.</summary>
    public static bool Handles(string action) => action switch
    {
        "buyMarket" or "sellMarket" or "buyLimit" or "sellLimit" or "flatten" or "flattenAccount"
            or "cancelOrders" or "cancelWorkingOrders" or "reverse" or "breakeven"
            or "attachBracket" or "moveStop" or "moveTarget" => true,
        _ => false,
    };

    public BridgeMessage Dispatch(BridgeMessage cmd)
    {
        SdLog.Debug("[REQ:{0}] Dispatching: {1}", cmd.RequestId ?? "n/a", cmd.Action);

        try
        {
            return cmd.Action switch
            {
                "buyMarket" => _engine.BuyMarket(cmd),
                "sellMarket" => _engine.SellMarket(cmd),
                _ => BridgeMessage.Failure(cmd.RequestId, cmd.Action, "UNSUPPORTED_ACTION",
                         $"Action '{cmd.Action}' is not implemented yet by the ATAS strategy."),
            };
        }
        catch (Exception ex)
        {
            // Scoped to ONE command on purpose. An unexpected exception must cost that command, not
            // the session: the bridge would reconnect, the host would replay the same command, and
            // one malformed field would become a permanent loop.
            SdLog.Fail("Command", ex, "[REQ:{0}] Unhandled exception in {1}", cmd.RequestId ?? "n/a", cmd.Action);
            return BridgeMessage.Failure(cmd.RequestId, cmd.Action, "INTERNAL_ERROR", $"Unexpected error: {ex.Message}");
        }
    }
}
```

- [x] **Étape 3 : écrire le moniteur d'ordres**

Créer `src/Atas.Strategy.TradeDeck/OrderMonitor.cs` :

```csharp
using System;
using System.Collections.Generic;
using System.Linq;

using ATAS.DataFeedsCore;

namespace TradeDeck.Atas.Strategy;

/// <summary>
/// Watches every order on every connector the deck can reach, and tells the deck what it cannot see
/// for itself.
///
/// THE REJECTION PATH IS THE REASON THIS CLASS EXISTS. Submission is asynchronous: OpenOrder returns
/// long before the venue answers, so without this the key would flash "OK" for an order that never
/// reached the market. This is exactly the pain Account.Submit caused on the NinjaTrader side.
///
/// SUBSCRIBED TO EVERY CONNECTOR THE REGISTRY REPORTS, NOT ONE. There is no single connector to
/// subscribe to (task 3): portfolios are spread across whichever connectors are actually connected,
/// and an Order/Position/MyTrade belongs to whichever one carries its portfolio. Missing a connector
/// here means missing every external order placed on the account it carries — exactly the hole
/// GuardEnforcer (task 8) exists to close, so <see cref="Refresh"/> is called every publish tick
/// (500 ms) rather than once at start-up: a connector can connect mid-session, as the Rithmic one did
/// in the field trial, carrying a portfolio invisible from the chart.
/// </summary>
internal sealed class OrderMonitor : IDisposable
{
    private readonly BridgeClient _bridge;
    private readonly object _lock = new();

    /// <summary>Connectors currently subscribed, keyed by reference — a connector never compares by value.</summary>
    private readonly HashSet<IDataFeedConnector> _attached = new();
    private bool _disposed;

    /// <summary>
    /// Refreshes the deck on the spot instead of waiting for the next publish tick. Assigned rather
    /// than injected so this class keeps no reference back to the publisher that owns it.
    /// </summary>
    public Action? StateChanged { get; set; }

    public OrderMonitor(BridgeClient bridge) => _bridge = bridge;

    /// <summary>
    /// Reconciles the connectors this monitor listens to against what the registry currently reports.
    /// Cheap to call every tick: a session with no connector churn does nothing beyond a set
    /// comparison, and one that gains or loses a connector attaches or detaches exactly that one.
    /// </summary>
    public void Refresh(IReadOnlyList<IDataFeedConnector> connectors)
    {
        lock (_lock)
        {
            if (_disposed) return;

            var wanted = new HashSet<IDataFeedConnector>(connectors);

            foreach (var gone in _attached.Where(c => !wanted.Contains(c)).ToList())
                DetachOne(gone);

            foreach (var connector in connectors)
                if (!_attached.Contains(connector)) AttachOne(connector);
        }
    }

    /// <summary>Detaches every connector — used on shutdown and in degraded mode, where the registry reports none.</summary>
    public void DetachAll()
    {
        lock (_lock)
        {
            foreach (var connector in _attached.ToList()) DetachOne(connector);
        }
    }

    private void AttachOne(IDataFeedConnector connector)
    {
        connector.OrderChanged += OnOrderChanged;
        connector.OrdersRegisterFailed += OnRegisterFailed;
        connector.OrdersCancelFailed += OnCancelFailed;
        connector.OrderModifyFailed += OnModifyFailed;
        connector.PositionsChanged += OnPositionsChanged;

        _attached.Add(connector);
        SdLog.Event("Order", "Order monitor attached to connector {0} ({1} connector(s) now watched)",
            connector.GetType().Name, _attached.Count);
    }

    private void DetachOne(IDataFeedConnector connector)
    {
        try
        {
            connector.OrderChanged -= OnOrderChanged;
            connector.OrdersRegisterFailed -= OnRegisterFailed;
            connector.OrdersCancelFailed -= OnCancelFailed;
            connector.OrderModifyFailed -= OnModifyFailed;
            connector.PositionsChanged -= OnPositionsChanged;
        }
        catch (Exception ex) { SdLog.Warn("Could not unsubscribe order updates from a connector: {0}", ex.Message); }

        _attached.Remove(connector);
        SdLog.Event("Order", "Order monitor detached from connector {0} ({1} connector(s) now watched)",
            connector.GetType().Name, _attached.Count);
    }

    private void OnPositionsChanged(IDataFeedConnector connector, IEnumerable<Position> positions)
    {
        // The position actually moved — this is the moment the deck must be refreshed, rather than
        // up to 500 ms later.
        Safely("Position", () =>
        {
            SdLog.TraceEvent("Position", "PositionsChanged received — publishing state immediately");
            StateChanged?.Invoke();
        });
    }

    private void OnOrderChanged(IDataFeedConnector connector, Order order)
    {
        Safely("Order", () =>
        {
            if (order == null) return;

            var status = order.Status();

            // Every transition is recorded, not only the failures: reconstructing why a position
            // ended up where it did needs the whole life of the order. Both the raw state and the
            // Status() verdict go in — the second is what we act on, the first is what the platform
            // will quote back at you in a support thread.
            SdLog.Event("Order", "Order {0} {1}/{2} — {3} {4} qty={5} unfilled={6} instrument={7}",
                order.Id ?? "?", status, order.State, order.Direction, order.Type,
                order.QuantityToFill, order.Unfilled,
                ContextResolver.InstrumentName(order.Security));

            if (order.State == OrderStates.Failed)
            {
                // A failed order with no message of its own still has to reach the deck: the key
                // must go from "sent" to "rejected", and silence would leave it lying. Read from
                // State rather than Status(), which has no value for "the venue refused it".
                ReportRejection(order, "OrderFailed", order.State.ToString());
                StateChanged?.Invoke();
                return;
            }

            // These change the stop/target picture the deck draws even when the net position does
            // not move — a target cancelled, a stop replaced after a break-even.
            if (status is OrderStatus.Filled or OrderStatus.PartlyFilled or OrderStatus.Canceled)
                StateChanged?.Invoke();
        });
    }

    private void OnRegisterFailed(IDataFeedConnector connector, string message, Order order) =>
        Safely("Order", () => { ReportRejection(order, "OrderRejected", message); StateChanged?.Invoke(); });

    private void OnCancelFailed(IDataFeedConnector connector, string message, Order order) =>
        Safely("Order", () => ReportRejection(order, "CancelRejected", message));

    private void OnModifyFailed(IDataFeedConnector connector, Order order, Order newOrder, string error) =>
        Safely("Order", () => ReportRejection(order, "ModifyRejected", error));

    /// <summary>
    /// Sends the orderUpdate event the host turns into a red REJECTED on the key.
    ///
    /// The field names are the protocol's and are read by the host as they are: orderState,
    /// rejected, error, reason. `rejected: true` is what makes the key flash — the key's background
    /// stays normal, because a greyed background means "blocked", never "rejected".
    /// </summary>
    private void ReportRejection(Order? order, string error, string? reason)
    {
        var payload = new Dictionary<string, object?>
        {
            ["orderId"] = order?.Id ?? string.Empty,
            ["orderState"] = "Rejected",
            ["rejected"] = true,
            ["error"] = error,
            ["reason"] = string.IsNullOrWhiteSpace(reason) ? error : reason,
            ["quantity"] = order != null ? (int)order.QuantityToFill : 0,
            ["orderType"] = order?.Type.ToString() ?? "?",
            ["orderAction"] = order?.Direction.ToString() ?? "?",
            ["name"] = order?.Comment ?? string.Empty,
            ["instrument"] = ContextResolver.InstrumentName(order?.Security),
        };

        SdLog.EventWarn("Order", "ORDER REJECTED {0} — {1} {2} qty={3} instrument={4}: {5} (error={6})",
            order?.Id ?? "?", order?.Direction, order?.Type, order?.QuantityToFill,
            ContextResolver.InstrumentName(order?.Security), reason, error);

        // Fire-and-forget: this runs on the connector's event thread and cannot wait. SendAsync
        // catches its own errors, so no faulted task is left unobserved.
        _ = _bridge.SendAsync(BridgeMessage.Notify("orderUpdate", payload));
    }

    /// <summary>
    /// Runs a handler with no way for it to throw back into the connector's event pipeline. An
    /// exception escaping here would land in the platform's own dispatch loop, and a lost log line
    /// must never cost a trading session.
    /// </summary>
    private static void Safely(string category, Action action)
    {
        try { action(); }
        catch (Exception ex) { SdLog.Fail(category, ex, "Error handling a connector event"); }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            DetachAll();
            StateChanged = null;
        }
    }
}
```

- [x] **Étape 4 : câbler le tout dans la stratégie**

Dans `TradeDeckStrategy.cs`, ajouter `using ATAS.DataFeedsCore;` en tête (le type `Order` y vit),
puis déclarer l'implémentation de la passerelle sur la classe :

```csharp
public class TradeDeckStrategy : ChartStrategy, IOrderGateway
```

ajouter les champs :

```csharp
    private OrderEngine? _engine;
    private CommandDispatcher? _dispatcher;
    private OrderMonitor? _monitor;

    /// <summary>
    /// Keeps the order monitor's connector subscriptions in step with the registry, at the same
    /// cadence as the state publisher. A connector can connect or disconnect mid-session — see
    /// OrderMonitor.Refresh — and the only way to notice is to ask again periodically; there is no
    /// "connector list changed" event to subscribe to instead.
    /// </summary>
    private System.Threading.Timer? _connectorRefreshTimer;
```

et les cinq membres de la passerelle, en fin de classe :

```csharp
    // --- IOrderGateway ---------------------------------------------------------------
    //
    // The three verbs come from ChartStrategy itself, which is the documented strategy API and takes
    // the order and nothing else. The two classifications only ITradingManager can make, and it is a
    // direct property of Indicator — ATAS's own indicators write TradingManager.Portfolio just like
    // this, with no detour through DataProvider.

    void IOrderGateway.Open(Order order) => OpenOrder(order);

    void IOrderGateway.Cancel(Order order) => CancelOrder(order);

    void IOrderGateway.Modify(Order oldOrder, Order newOrder) => ModifyOrder(oldOrder, newOrder);

    /// <summary>
    /// Classifies an order the deck did not place. Never throws: the caller is the journal, and a
    /// classification it could not make must cost a field, never the line.
    /// </summary>
    bool IOrderGateway.IsStopLossOrder(Order order)
    {
        try { return TradingManager?.IsStopLossOrder(order) == true; }
        catch { return false; }
    }

    bool IOrderGateway.IsTakeProfitOrder(Order order)
    {
        try { return TradingManager?.IsTakeProfitOrder(order) == true; }
        catch { return false; }
    }
```

Dans `OnStarted()`, après la création du résolveur et **avant** celle du publieur :

```csharp
            _engine = new OrderEngine(_resolver, this);
            _dispatcher = new CommandDispatcher(_engine);
            _monitor = new OrderMonitor(_bridge);
```

Après la création du publieur :

```csharp
            // Fills and cancellations refresh the deck on the spot instead of waiting for the next
            // publish tick. Set here rather than injected so the monitor keeps no reference back to
            // the publisher that owns it.
            _monitor.StateChanged = _publisher.PublishNow;

            // NEVER ChartStrategy.Connector (always null, confirmed by measurement — task 3). The
            // monitor's subscriptions are reconciled against the registry's CURRENT connector list on
            // the same cadence as the state publisher: a connector can appear mid-session (the
            // Rithmic connector in the 24/08 field trial connected after the chart had already
            // started), and there is no event to learn that from — only asking again.
            _connectorRefreshTimer = new System.Threading.Timer(_ =>
            {
                try { _monitor?.Refresh(_resolver?.Connectors ?? Array.Empty<IDataFeedConnector>()); }
                catch (Exception ex) { SdLog.Fail("Session", ex, "Order monitor connector refresh failed"); }
            }, null, 0, StateIntervalMs);
```

⚠ **Conséquence du mode dégradé à connaître avant la tâche 8, pas à découvrir dessus.**
`ContextResolver.Connectors` est **vide** en mode dégradé (registre introuvable) : `_monitor.Refresh`
détache alors tout, et plus aucun événement `OrderChanged` / `OrdersRegisterFailed` / `NewOrders`
n'arrive. Deux conséquences distinctes :
- **la remontée de rejet (tâche 4, étape 8 de ce plan / étape 4 de la recette)** ne fonctionne plus
  en mode dégradé — un ordre du deck refusé par le courtier ne produira pas de `orderUpdate`
  `rejected:true` tant que le registre reste introuvable ;
- **`GuardEnforcer` (tâche 8) perd sa seule source d'événements temps réel.** Ce n'est pas un défaut à
  corriger dans cette tâche : `TradingManager.Orders`, confirmé peuplé même hors registre (essai
  terrain du 24/08, un ordre `ChartTrader` manuel y était visible), reste consultable **depuis la
  boucle de publication** — mais l'appliquer à `GuardEnforcer` en mode dégradé exigerait un mécanisme
  de sondage que ce plan ne spécifie pas (voir la tâche 8). À signaler explicitement dans le rapport
  de tâche plutôt qu'à laisser croire que le mode dégradé protège autant que le mode normal.

Dans `OnStopping()`, avant l'arrêt du publieur :

```csharp
        try { _connectorRefreshTimer?.Dispose(); _connectorRefreshTimer = null; }
        catch (Exception ex) { SdLog.Fail("Session", ex, "Error while stopping the connector refresh timer"); }
        try { _monitor?.Dispose(); }
        catch (Exception ex) { SdLog.Fail("Session", ex, "Error while detaching the order monitor"); }
```

et dans `OnStopped()` : `_monitor = null; _dispatcher = null; _engine = null;`.

Enfin, dans `OnBridgeMessage`, insérer la branche des commandes marchandes **en tête** du `switch` :

```csharp
        var response = message.Action switch
        {
            _ when CommandDispatcher.Handles(message.Action) && _dispatcher != null => _dispatcher.Dispatch(message),
            "setAccount" or "setInstrument" => HandleTracking(message),
            "setCopierConfig" or "copierPanic" or "configureTrend" => Unsupported(message),
            _ => BridgeMessage.Failure(message.RequestId, message.Action,
                     "UNSUPPORTED_ACTION", $"Action '{message.Action}' is not supported by the ATAS strategy."),
        };
```

- [x] **Étape 5 : compiler**

```bash
dotnet build src/Atas.Strategy.TradeDeck/Atas.Strategy.TradeDeck.csproj -c Release
```

Attendu : `Build succeeded`.

- [x] **Étape 6 : LE contrôle qui décide du portage — un ordre part-il là où le deck vise ?**

C'est l'hypothèse porteuse de toute la conception, et **ni la documentation ni l'essai terrain du
24/08 ne la tranchent** — celui-ci a mesuré `ChartStrategy.Connector` toujours nul et le registre de
connecteurs joignable, mais n'a jamais soumis d'ordre visant un compte ou un instrument différent de
ceux du graphique. `ChartStrategy.Portfolio` et `.Security` sont fixés par le graphique — leur
accesseur en écriture **lève** — donc la seule façon de viser ailleurs est de poser `Portfolio` et
`Security` sur l'`Order` lui-même. L'exemple officiel `SmaChartStrategy` les recopie depuis la
stratégie et ne dit donc rien du cas qui nous intéresse : `OpenOrder` honore-t-il les champs de
l'ordre, ou leur substitue-t-il ceux du graphique ?

**Deux tests, dans cet ordre, sur compte de simulation** — le second est le plus important, parce que
c'est lui qui décide si l'agrégation multi-connecteurs de la tâche 3 sert à quelque chose pour
l'envoi d'ordres, pas seulement pour l'affichage :

1. **Même connecteur, instrument différent.** Ouvrir un graphique sur un instrument **A** (par
   exemple MNQ) et y démarrer la stratégie ; sélectionner sur le deck un instrument **B différent**
   (par exemple MES, même compte, même connecteur) ; appuyer une fois sur Acheter.
   Attendu : **la position s'ouvre sur B**, pas sur A.
2. **Connecteur différent.** Sans changer le graphique, sélectionner sur le deck un **compte porté
   par un AUTRE connecteur** que celui du graphique — dans l'essai terrain, le graphique était sur
   `DEMO29ADC` (`SbeConnector`) et `LDE050-SZ976P0C-TEST001` était porté par un `RithmicConnector`
   distinct. Appuyer une fois sur Acheter.
   Attendu : **la position s'ouvre sur ce second compte**, chez son propre courtier.

Vérifier les deux dans la fenêtre des positions d'ATAS, pas seulement sur la touche du deck.

**Si l'un des deux échoue, la conception ne tient pas telle quelle — et il n'existe PAS de repli
prêt à écrire dans ce plan.** L'idée d'origine — passer par une méthode brute du connecteur
(`Connector.RegisterOrder(order)`) — est elle-même invalidée par l'essai terrain :
`ChartStrategy.Connector` est **toujours nul**, donc cet appel ne peut littéralement pas s'exécuter,
et la surface d'API relevée par réflexion sur `IDataFeedConnector` (voir « Surface d'API ATAS utilisée
par ce plan ») ne liste **aucune** méthode d'envoi d'ordre — seulement `Portfolios`, `Securities`,
`Orders`, `Positions`, `MyTrades`, `GetPosition` et des événements. Aucune méthode de soumission n'a
été confirmée par réflexion sur le connecteur retourné par `ConnectorRegistry` : en inventer une ici
reproduirait exactement l'erreur qui a fait échouer ce test. **S'arrêter, journaliser précisément ce
qui a été observé (sur quel compte/instrument la position s'est réellement ouverte), et relancer une
sonde de réflexion sur l'instance de connecteur obtenue via `ConnectorRegistry.ConnectorFor(...)`**
avant d'écrire quoi que ce soit — c'est une nouvelle tâche de découverte, pas une ligne de code à
deviner.

Faire ensuite le contrôle du dialogue, qui ne coûte rien : appuyer sur Acheter et vérifier
qu'**aucune boîte de confirmation ne s'ouvre**. Le chemin documenté n'a pas de paramètre
`askConfirmation`, donc rien ne devrait apparaître ; si quelque chose apparaît, c'est un réglage de
la plateforme et non du code — le noter dans le rapport de tâche.

Consigner le résultat des trois contrôles : c'est la conclusion la plus réutilisable de tout ce
portage.

- [x] **Étape 7 : recette étape 3 — un ordre au marché**

Bridge lancé, hôte lancé, ATAS ouvert sur un **compte Sim**, stratégie démarrée, instrument
sélectionné sur le deck.

Appuyer **une fois** sur la touche Acheter.

Attendu, chaque point constaté :
- ATAS affiche un ordre puis une position ;
- la touche du deck passe en position (`LONG 1`, prix moyen affiché) ;
- `addon-*.log` : `Command | [REQ:… ] buyMarket completed (…ms)` puis
  `Order | Order … Done — Buy Market qty=1 …` ;
- le journal du bridge montre la réponse `"success":true`.

Puis aplatir la position **à la souris dans ATAS** et vérifier que le deck repasse à `FLAT` dans la
demi-seconde : c'est la preuve que `PositionsChanged` remonte bien.

- [x] **Étape 8 : recette étape 4 — un rejet remonte jusqu'à la touche**

Provoquer un rejet volontaire. Deux façons, la première d'abord :
- envoyer une quantité qui dépasse la taille de lot maximale de l'instrument (touche Quantité montée
  au-delà de `Security.LotMaxSize`), **ou**
- passer un ordre sur un instrument dont le marché est fermé.

Attendu :
- la touche affiche `REJECTED` pendant 5 s, **fond normal** (un fond grisé voudrait dire « bloqué ») ;
- `addon-*.log` : `Order | ORDER REJECTED … : <motif> (error=OrderRejected)` ;
- le journal du bridge montre l'événement `orderUpdate` avec `"rejected":true`.

**C'est le point qui prouve le portage.** S'il ne remonte pas, vérifier d'abord que
`OrdersRegisterFailed` est bien branché — ATAS peut aussi n'émettre qu'un `OrderChanged` avec
`State == Failed`, que le moniteur traite déjà.

- [x] **Étape 9 : committer**

```bash
git add src/Atas.Strategy.TradeDeck/ binaries/
git commit -m "Passer des ordres au marche depuis ATAS et remonter les rejets"
```

---

## Tâche 5 : ordres limites et `NO_MARKET_DATA`

Fin de tâche : **étape 5 de la recette**. Ce garde-fou n'est pas décoratif — un ordre limite envoyé
sans flux partirait à quelques ticks de 0, et une limite vendeuse sous le marché s'exécute
instantanément au marché.

**Fichiers :**
- Modifier : `src/Atas.Strategy.TradeDeck/OrderEngine.cs`
- Modifier : `src/Atas.Strategy.TradeDeck/CommandDispatcher.cs`

**Interfaces :**
- Consomme : `OrderEngine.Resolve/NewOrder/Submit/RoundToTick`, `ContextResolver.MarketPrice` (tâche 4).
- Produit : `OrderEngine.BuyLimit(BridgeMessage)`, `OrderEngine.SellLimit(BridgeMessage)`.

- [x] **Étape 1 : ajouter les deux méthodes au moteur**

Dans `OrderEngine.cs`, après `SubmitMarket` :

```csharp
    public BridgeMessage BuyLimit(BridgeMessage cmd) => SubmitLimit(cmd, OrderDirections.Buy);
    public BridgeMessage SellLimit(BridgeMessage cmd) => SubmitLimit(cmd, OrderDirections.Sell);

    private BridgeMessage SubmitLimit(BridgeMessage cmd, OrderDirections direction)
    {
        var ctx = Resolve(cmd);
        if (ctx.Error != null) return ctx.Error;

        var quantity = cmd.GetInt("quantity") ?? 1;
        var offsetTicks = cmd.GetInt("offsetTicks") ?? 0;

        if (quantity < 1)
            return BridgeMessage.Failure(cmd.RequestId, cmd.Action, "INVALID_QUANTITY", "Quantity must be >= 1");

        var name = ContextResolver.InstrumentName(ctx.Security);

        var tickSize = ctx.Security!.TickSize;
        if (tickSize <= 0m)
        {
            return BridgeMessage.Failure(cmd.RequestId, cmd.Action, "NO_MARKET_DATA",
                $"Invalid tick size for {name} — cannot compute a limit price.");
        }

        // WITHOUT A FEED THIS IS 0, AND 0 IS NOT A PRICE. A limit computed from it would land a few
        // ticks from zero: a sell limit there fills instantly AT MARKET, which is the opposite of
        // what the trader asked for. The order is refused instead of sent.
        var market = ContextResolver.MarketPrice(ctx.Security);
        if (market <= 0m)
        {
            return BridgeMessage.Failure(cmd.RequestId, cmd.Action, "NO_MARKET_DATA",
                $"No market data for {name} — cannot compute a limit price. Open a chart or a DOM for this instrument.");
        }

        var limitPrice = RoundToTick(market + offsetTicks * tickSize, tickSize);
        if (limitPrice <= 0m)
        {
            return BridgeMessage.Failure(cmd.RequestId, cmd.Action, "NO_MARKET_DATA",
                $"Computed limit price {limitPrice} is not valid for {name}.");
        }

        var order = NewOrder(ctx, direction, OrderTypes.Limit, quantity, DeckOrderComment);
        order.Price = limitPrice;

        var failure = Submit(cmd, order);
        if (failure != null) return failure;

        SdLog.Event("Order", "[REQ:{0}] {1} {2} {3} Limit @ {4} submitted on {5} (market={6}, offset={7} ticks)",
            cmd.RequestId, direction, quantity, name, limitPrice, ctx.Portfolio!.AccountID, market, offsetTicks);

        return BridgeMessage.Response(cmd.RequestId, cmd.Action, true, new
        {
            orderId = order.Id ?? string.Empty,
            limitPrice,
            message = $"{direction} {quantity} {name} Limit @ {limitPrice} submitted",
        });
    }
```

- [x] **Étape 2 : ouvrir les deux branches de l'aiguilleur**

Dans `CommandDispatcher.Dispatch`, ajouter sous `"sellMarket"` :

```csharp
                "buyLimit" => _engine.BuyLimit(cmd),
                "sellLimit" => _engine.SellLimit(cmd),
```

- [x] **Étape 3 : compiler**

```bash
dotnet build src/Atas.Strategy.TradeDeck/Atas.Strategy.TradeDeck.csproj -c Release
```

Attendu : `Build succeeded`.

- [ ] **Étape 4 : vérifier — recette étape 5**

Deux contrôles, dans cet ordre :

1. **Cas nominal.** Instrument avec flux (un DOM ou un graphique ouvert dessus), touche Acheter
   Limite avec `offsetTicks: -2`. Attendu : un ordre limite apparaît dans ATAS **deux ticks sous le
   marché**, et `addon-*.log` porte `Limit @ … (market=…, offset=-2 ticks)`. Vérifier à la main que
   `limitPrice = market − 2 × tickSize`, arrondi au tick.
2. **Cas `NO_MARKET_DATA`.** Sélectionner sur le deck un instrument **sans souscription de flux** —
   un contrat qu'aucun graphique ni DOM n'affiche. Appuyer sur Acheter Limite.
   Attendu : **aucun ordre n'est envoyé**, la touche affiche l'erreur, et `addon-*.log` porte
   `Command | … buyLimit refused: NO_MARKET_DATA — No market data for …`.

Le second contrôle est le plus important des deux : c'est celui qui empêche une limite vendeuse de
partir à zéro.

- [x] **Étape 5 : committer**

```bash
git add src/Atas.Strategy.TradeDeck/ binaries/
git commit -m "Ordres limites depuis ATAS, avec le refus NO_MARKET_DATA"
```

---

## Tâche 6 : gestion de position

Cinq commandes : `flatten`, `flattenAccount`, `cancelWorkingOrders`, `cancelOrders`, `reverse`.

**Fichiers :**
- Modifier : `src/Atas.Strategy.TradeDeck/OrderEngine.cs`
- Modifier : `src/Atas.Strategy.TradeDeck/CommandDispatcher.cs`

**Interfaces :**
- Consomme : `OrderEngine.Resolve/NewOrder/Submit/Cancel`, `ContextResolver.FindPosition/
  FindActiveOrders/FindAllActiveOrders` (tâches 3 et 4).
- Produit : `OrderEngine.Flatten`, `.FlattenAccount`, `.CancelWorkingOrders`, `.CancelOrders`,
  `.Reverse`, toutes de signature `BridgeMessage Xxx(BridgeMessage cmd)`.

- [x] **Étape 1 : ajouter les cinq méthodes**

Dans `OrderEngine.cs`, après les ordres d'entrée :

```csharp
    /// <summary>
    /// Closes the position on the selected instrument with an opposite market order.
    ///
    /// An explicit order rather than Connector.ClosePositionAsync, and for the same reason the
    /// trading manager's SetStopLoss is avoided: what the deck aims at must never depend on what a
    /// helper decides to look at. The order carries the deck's portfolio and security, and its size
    /// is the position's own — nothing is inferred.
    ///
    /// Idempotent by design: flat means success with nothing sent. A trader pressing Flatten twice
    /// must not open a short.
    /// </summary>
    public BridgeMessage Flatten(BridgeMessage cmd)
    {
        var ctx = Resolve(cmd);
        if (ctx.Error != null) return ctx.Error;

        var name = ContextResolver.InstrumentName(ctx.Security);
        var position = _resolver.FindPosition(ctx.Portfolio, ctx.Security);

        if (position == null)
        {
            return BridgeMessage.Response(cmd.RequestId, cmd.Action, true, new
            {
                positionsFlattened = 0,
                message = $"No open position for {name}",
            });
        }

        var quantity = Math.Abs(position.Volume);
        var direction = position.Volume > 0m ? OrderDirections.Sell : OrderDirections.Buy;

        var order = NewOrder(ctx, direction, OrderTypes.Market, quantity, DeckOrderComment + "_Flatten");
        var failure = Submit(cmd, order);
        if (failure != null) return failure;

        SdLog.Event("Position", "[REQ:{0}] Flatten {1} on {2} — {3} {4}",
            cmd.RequestId, name, ctx.Portfolio!.AccountID, direction, quantity);

        return BridgeMessage.Response(cmd.RequestId, cmd.Action, true, new
        {
            positionsFlattened = 1,
            message = $"Flatten {name} submitted",
        });
    }

    /// <summary>
    /// Closes EVERY position on the account and cancels every working order on it.
    ///
    /// Distinct from Flatten, which only touches the selected instrument. This one backs the
    /// automatic liquidation on daily loss, and there the ACCOUNT is the unit that matters: the
    /// limit is computed on account P&amp;L, so announcing "the day is over" while a position stays
    /// open on an instrument the trader was not watching would be worse than doing nothing — he
    /// would believe he was flat.
    ///
    /// ORDERS ARE CANCELLED FIRST, and account-wide, including on instruments carrying no position:
    /// a resting entry that fills afterwards would reopen exactly the exposure just closed.
    ///
    /// Never reachable from a key press. Its only caller is the safety macro, via the bridge.
    /// </summary>
    public BridgeMessage FlattenAccount(BridgeMessage cmd)
    {
        var accountName = cmd.GetString("account");
        if (string.IsNullOrWhiteSpace(accountName))
            return BridgeMessage.Failure(cmd.RequestId, cmd.Action, "CONTEXT_MISSING", "Account name is required.");

        var portfolio = _resolver.FindPortfolio(accountName);
        if (portfolio == null)
            return BridgeMessage.Failure(cmd.RequestId, cmd.Action, "ACCOUNT_NOT_FOUND", $"Account '{accountName}' not found.");

        var orders = _resolver.FindAllActiveOrders(portfolio);
        var cancelFailure = orders.Count > 0 ? Cancel(cmd, orders) : null;
        if (cancelFailure != null) return cancelFailure;

        var flattened = 0;
        // FindAllPositions, NOT a raw connector: the portfolio can live on any connector the registry
        // aggregates (task 3), and in degraded mode this call transparently falls back to the
        // chart's own TradingManager.Position — no branch needed here for that case.
        var positions = _resolver.FindAllPositions(portfolio);
        foreach (var position in positions)
        {
            if (position == null || position.Volume == 0m) continue;
            if (position.Security == null) continue;

            var ctx = new TradingContext { Portfolio = portfolio, Security = position.Security };
            var direction = position.Volume > 0m ? OrderDirections.Sell : OrderDirections.Buy;
            var order = NewOrder(ctx, direction, OrderTypes.Market, Math.Abs(position.Volume),
                                 DeckOrderComment + "_Liquidate");

            var failure = Submit(cmd, order);
            if (failure != null)
            {
                // The worst outcome this whole feature can produce: the trader is told the day
                // is closed while a position is still live. Reported as a failure so the deck
                // turns red instead of pretending it worked.
                SdLog.Fail("Safety", null, "[REQ:{0}] ACCOUNT LIQUIDATION FAILED on {1} for {2}",
                    cmd.RequestId, accountName, ContextResolver.InstrumentName(position.Security));
                return failure;
            }

            flattened++;
        }

        SdLog.EventWarn("Safety", "[REQ:{0}] ACCOUNT LIQUIDATION on {1} — {2} order(s) cancelled, {3} position(s) flattened",
            cmd.RequestId, accountName, orders.Count, flattened);

        return BridgeMessage.Response(cmd.RequestId, cmd.Action, true, new
        {
            ordersCancelled = orders.Count,
            positionsFlattened = flattened,
            message = $"Account {accountName} liquidated: {flattened} position(s), {orders.Count} order(s)",
        });
    }

    /// <summary>
    /// Cancels the working orders of the instrument WITHOUT touching the position.
    /// Idempotent: success with ordersCancelled = 0 when there is nothing to cancel.
    /// </summary>
    public BridgeMessage CancelWorkingOrders(BridgeMessage cmd)
    {
        var ctx = Resolve(cmd);
        if (ctx.Error != null) return ctx.Error;

        var name = ContextResolver.InstrumentName(ctx.Security);
        var orders = _resolver.FindActiveOrders(ctx.Portfolio, ctx.Security);

        if (orders.Count == 0)
        {
            return BridgeMessage.Response(cmd.RequestId, cmd.Action, true, new
            {
                ordersCancelled = 0,
                message = $"No working order for {name}",
            });
        }

        var failure = Cancel(cmd, orders);
        if (failure != null) return failure;

        SdLog.Event("Order", "[REQ:{0}] Cancelled {1} working order(s) for {2} on {3} (position untouched)",
            cmd.RequestId, orders.Count, name, ctx.Portfolio!.AccountID);

        return BridgeMessage.Response(cmd.RequestId, cmd.Action, true, new
        {
            ordersCancelled = orders.Count,
            message = $"Cancelled {orders.Count} order(s) for {name}",
        });
    }

    /// <summary>
    /// "Close All": cancels every pending order AND closes the position. Kept deliberately
    /// destructive — this backs the Close All key.
    ///
    /// Cancel first, then flatten. The other order would leave a resting entry able to reopen the
    /// position the flatten had just closed.
    /// </summary>
    public BridgeMessage CancelOrders(BridgeMessage cmd)
    {
        var ctx = Resolve(cmd);
        if (ctx.Error != null) return ctx.Error;

        var name = ContextResolver.InstrumentName(ctx.Security);
        var orders = _resolver.FindActiveOrders(ctx.Portfolio, ctx.Security);

        if (orders.Count > 0)
        {
            var cancelFailure = Cancel(cmd, orders);
            if (cancelFailure != null) return cancelFailure;
        }

        var flatten = Flatten(cmd);
        if (flatten.Error != null) return flatten;

        SdLog.Event("Position", "[REQ:{0}] Close All on {1} — {2} order(s) cancelled, position closed",
            cmd.RequestId, name, orders.Count);

        return BridgeMessage.Response(cmd.RequestId, cmd.Action, true, new
        {
            ordersCancelled = orders.Count,
            message = $"Cancelled all orders and closed position for {name}",
        });
    }

    /// <summary>
    /// Reverses the position: cancel the protections, then ONE market order of twice the size.
    ///
    /// The protective orders of the position being closed go first. Submitting the reversal alone
    /// leaves them working, and a stop from the old long is a SELL: after the reversal it would ADD
    /// to the new short instead of protecting it.
    ///
    /// One order of 2× rather than a close followed by an open: two orders leave a window in which
    /// the account is flat and the second one has not landed, and a partial fill in that window
    /// leaves a size nobody chose.
    /// </summary>
    public BridgeMessage Reverse(BridgeMessage cmd)
    {
        var ctx = Resolve(cmd);
        if (ctx.Error != null) return ctx.Error;

        var position = _resolver.FindPosition(ctx.Portfolio, ctx.Security);
        if (position == null)
            return BridgeMessage.Failure(cmd.RequestId, cmd.Action, "NO_POSITION", "No open position to reverse.");

        var currentQuantity = Math.Abs(position.Volume);
        var wasLong = position.Volume > 0m;

        var stale = _resolver.FindActiveOrders(ctx.Portfolio, ctx.Security);
        if (stale.Count > 0)
        {
            var cancelFailure = Cancel(cmd, stale);
            if (cancelFailure != null) return cancelFailure;
            SdLog.Event("Order", "[REQ:{0}] Reverse: cancelled {1} working order(s) before reversing",
                cmd.RequestId, stale.Count);
        }

        var direction = wasLong ? OrderDirections.Sell : OrderDirections.Buy;
        var order = NewOrder(ctx, direction, OrderTypes.Market, currentQuantity * 2m, DeckOrderComment + "_Reverse");

        var failure = Submit(cmd, order);
        if (failure != null) return failure;

        SdLog.Event("Position", "[REQ:{0}] Reverse {1} {2} → {3} {4}",
            cmd.RequestId, wasLong ? "Long" : "Short", currentQuantity, direction, currentQuantity * 2m);

        return BridgeMessage.Response(cmd.RequestId, cmd.Action, true, new
        {
            ordersCancelled = stale.Count,
            message = stale.Count > 0
                ? $"Reverse from {(wasLong ? "Long" : "Short")} {currentQuantity} → {direction} {currentQuantity * 2m} ({stale.Count} order(s) cancelled)"
                : $"Reverse from {(wasLong ? "Long" : "Short")} {currentQuantity} → {direction} {currentQuantity * 2m}",
        });
    }
```

- [x] **Étape 2 : ouvrir les cinq branches de l'aiguilleur**

Dans `CommandDispatcher.Dispatch`, ajouter :

```csharp
                "flatten" => _engine.Flatten(cmd),
                // No key produces this action: the safety macro is its only emitter.
                "flattenAccount" => _engine.FlattenAccount(cmd),
                "cancelWorkingOrders" => _engine.CancelWorkingOrders(cmd),
                "cancelOrders" => _engine.CancelOrders(cmd),
                "reverse" => _engine.Reverse(cmd),
```

- [x] **Étape 3 : compiler**

```bash
dotnet build src/Atas.Strategy.TradeDeck/Atas.Strategy.TradeDeck.csproj -c Release
```

Attendu : `Build succeeded`.

- [ ] **Étape 4 : vérifier, une commande à la fois — recette étape 6, première moitié**

Sur compte Sim, chaque contrôle repart d'une position ouverte quand il en faut une :

| Geste | Attendu |
|---|---|
| Position long 2, touche **Aplatir** | position à zéro, deck `FLAT`, log `Position \| … Flatten … Sell 2` |
| **À plat**, touche Aplatir | succès, **aucun ordre envoyé**, `positionsFlattened: 0` — presser deux fois ne doit jamais ouvrir un short |
| Ordre limite en attente, touche **Annuler les ordres** | l'ordre disparaît, la **position reste ouverte**, `ordersCancelled: 1` |
| Position + ordre limite, touche **Close All** | l'ordre disparaît **et** la position se ferme |
| Position long 2 avec un stop posé, touche **Inverser** | le stop est annulé **d'abord**, puis un ordre `Sell 4` part, et la position finit short 2 |

Le quatrième et le cinquième sont les deux qui comptent : dans les deux cas, un ordre resté actif
rouvrirait ou renforcerait la position au lieu de la protéger.

`flattenAccount` n'a pas de touche. Le déclencher en armant la macro de sécurité avec une
`dailyLossLimit` très basse et en provoquant une perte sur Sim, **ou** en envoyant la commande à la
main sur le port isolé du bridge. Attendu : toutes les positions du compte fermées, tous les ordres
annulés, et la ligne `Safety | … ACCOUNT LIQUIDATION on …` dans `addon-*.log`.

- [x] **Étape 5 : committer**

```bash
git add src/Atas.Strategy.TradeDeck/ binaries/
git commit -m "Gestion de position depuis ATAS : aplatir, annuler, liquider, inverser"
```

---

## Tâche 7 : les protections

Quatre commandes : `breakeven`, `attachBracket`, `moveStop`, `moveTarget`. La règle qui les gouverne
toutes : **on déplace TOUS les ordres de protection, jamais un au hasard** — sur une position
renforcée, en choisir un laissait le reste de la position sans protection au vieux niveau.

**Fichiers :**
- Modifier : `src/Atas.Strategy.TradeDeck/OrderEngine.cs`
- Modifier : `src/Atas.Strategy.TradeDeck/CommandDispatcher.cs`

**Interfaces :**
- Consomme : `OrderEngine.Resolve/NewOrder/Submit/Cancel/Modify/RoundToTick`,
  `ContextResolver.FindStopOrders/FindTargetOrders/FindPosition/MarketPrice` (tâches 3, 4 et 6).
- Produit : `OrderEngine.BreakEven`, `.AttachBracket`, `.MoveStop`, `.MoveTarget`, toutes de
  signature `BridgeMessage Xxx(BridgeMessage cmd)`.

- [ ] **Étape 1 : ajouter le vocabulaire des protections**

Dans `OrderEngine.cs`, sous `DeckOrderComment` :

```csharp
    /// <summary>
    /// Comments carried by the two protective orders the bracket macro owns.
    ///
    /// They are what separates "the stop this macro placed for this position" from "a stop the
    /// trader put there himself" — a manual stop, a break-even, a stop dragged on the chart. Only
    /// the first kind is ever repriced or resized: silently moving a stop the trader placed with his
    /// own hands would take away the protection he chose.
    ///
    /// Same strings as the NinjaTrader add-on, deliberately: the server already reads them in the
    /// journal to tell an exit at the stop from an exit at the target.
    /// </summary>
    public const string BracketStopComment = "StreamDeck_SL";
    public const string BracketTargetComment = "StreamDeck_TP";
    public const string BreakEvenComment = "StreamDeck_BE";
```

- [ ] **Étape 2 : écrire le break-even**

```csharp
    /// <summary>
    /// Moves every working stop to the average entry price, plus or minus an offset — and CREATES
    /// one when there is none.
    ///
    /// Creating rather than refusing is deliberate: requiring a pre-existing stop would leave every
    /// entry placed from the deck unprotected, while this side has everything it needs to submit the
    /// order itself.
    /// </summary>
    public BridgeMessage BreakEven(BridgeMessage cmd)
    {
        var ctx = Resolve(cmd);
        if (ctx.Error != null) return ctx.Error;

        var name = ContextResolver.InstrumentName(ctx.Security);
        var position = _resolver.FindPosition(ctx.Portfolio, ctx.Security);
        if (position == null)
            return BridgeMessage.Failure(cmd.RequestId, cmd.Action, "NO_POSITION", "No open position for break-even.");

        var tickSize = ctx.Security!.TickSize;
        if (tickSize <= 0m)
            return BridgeMessage.Failure(cmd.RequestId, cmd.Action, "NO_MARKET_DATA", $"Invalid tick size for {name}.");

        var offsetTicks = cmd.GetInt("offsetTicks") ?? 0;
        var isLong = position.Volume > 0m;
        var averagePrice = position.AveragePrice;

        var bePrice = RoundToTick(
            isLong ? averagePrice + offsetTicks * tickSize : averagePrice - offsetTicks * tickSize,
            tickSize);

        // A stop must stay on the protective side of the market. When the trade is at a loss,
        // break-even would put it on the wrong side, the venue rejects the change with an opaque
        // message, and the trader is left guessing — say so explicitly instead.
        //
        // 0 when there is no feed: the test is then SKIPPED rather than guessed. Not knowing the
        // market is not a reason to leave a position unprotected.
        var market = ContextResolver.MarketPrice(ctx.Security);
        if (market > 0m && (isLong ? bePrice >= market : bePrice <= market))
        {
            return BridgeMessage.Failure(cmd.RequestId, cmd.Action, "INVALID_STOP_PRICE",
                $"Break-even price {bePrice} is on the wrong side of the market ({market}) — the trade is not in profit yet.");
        }

        var stops = _resolver.FindStopOrders(ctx.Portfolio, ctx.Security);

        if (stops.Count == 0)
        {
            var quantity = Math.Abs(position.Volume);
            if (quantity <= 0m)
                return BridgeMessage.Failure(cmd.RequestId, cmd.Action, "NO_POSITION", "Position quantity is zero — nothing to protect.");

            // The stop EXITS the position, so it goes the opposite way to it.
            var exit = isLong ? OrderDirections.Sell : OrderDirections.Buy;
            var order = NewOrder(ctx, exit, OrderTypes.Stop, quantity, BreakEvenComment);
            order.TriggerPrice = bePrice;

            var failure = Submit(cmd, order);
            if (failure != null) return failure;

            SdLog.Event("Protection", "[REQ:{0}] BE{1}: CREATED protective {2} stop x{3} at {4} (avg entry {5})",
                cmd.RequestId, offsetTicks != 0 ? "+" + offsetTicks : "", exit, quantity, bePrice, averagePrice);

            return BridgeMessage.Response(cmd.RequestId, cmd.Action, true, new
            {
                bePrice, avgPrice = averagePrice, offsetTicks,
                stopsCreated = 1, stopsModified = 0,
                message = $"Break-even: protective stop created at {bePrice}",
            });
        }

        var moved = 0;
        foreach (var stop in stops)
        {
            var failure = Modify(cmd, stop, updated =>
            {
                // For a StopLimit, keep the original distance between trigger and limit: rebuilding
                // it would silently widen or tighten the slippage the trader accepted.
                var limitOffset = updated.Type == OrderTypes.StopLimit ? updated.Price - updated.TriggerPrice : 0m;
                updated.TriggerPrice = bePrice;
                if (updated.Type == OrderTypes.StopLimit) updated.Price = bePrice + limitOffset;
            });
            if (failure != null) return failure;
            moved++;
        }

        SdLog.Event("Protection", "[REQ:{0}] BE{1}: moved {2} stop(s) to {3} (avg entry {4})",
            cmd.RequestId, offsetTicks != 0 ? "+" + offsetTicks : "", moved, bePrice, averagePrice);

        return BridgeMessage.Response(cmd.RequestId, cmd.Action, true, new
        {
            bePrice, avgPrice = averagePrice, offsetTicks,
            stopsCreated = 0, stopsModified = moved,
            message = $"Break-even: {moved} stop(s) moved to {bePrice}",
        });
    }
```

- [ ] **Étape 3 : écrire le bracket**

```csharp
    /// <summary>
    /// Places the take profit and/or the stop loss of the open position, computed in ticks from its
    /// AVERAGE PRICE and in the direction of the position.
    ///
    /// Sent by the host's Auto TP/SL automatism once the position actually exists, never alongside
    /// the entry: submission is asynchronous and returns long before the fill, so an entry price
    /// read at submit time would be a guess. The average price is the only value that is true — and
    /// it is what makes a scale-in work, since resending the same command is enough for both legs to
    /// follow the new average.
    ///
    /// 0 disables a leg. It means "do not place this one", NEVER "cancel what is already there": a
    /// trader setting his take profit back to 0 is saying the macro should stop managing it, not
    /// that the protection currently working should vanish.
    ///
    /// Both legs go out under the same OCO group. Without it, a filled take profit leaves the stop
    /// working on a flat position — and a stop on a flat position is an ENTRY the moment it
    /// triggers, opening the reverse of the trade just closed.
    /// </summary>
    public BridgeMessage AttachBracket(BridgeMessage cmd)
    {
        var ctx = Resolve(cmd);
        if (ctx.Error != null) return ctx.Error;

        var name = ContextResolver.InstrumentName(ctx.Security);
        var position = _resolver.FindPosition(ctx.Portfolio, ctx.Security);
        if (position == null)
            return BridgeMessage.Failure(cmd.RequestId, cmd.Action, "NO_POSITION", "No open position to attach a bracket to.");

        var stopTicks = cmd.GetInt("stopLossTicks") ?? 0;
        var targetTicks = cmd.GetInt("takeProfitTicks") ?? 0;

        // A negative distance would put the stop on the profit side and the target on the loss side:
        // two instantly marketable orders, both closing the trade by surprise.
        if (stopTicks < 0 || targetTicks < 0)
        {
            return BridgeMessage.Failure(cmd.RequestId, cmd.Action, "INVALID_PAYLOAD",
                "stopLossTicks and takeProfitTicks must be zero (leg disabled) or positive.");
        }

        if (stopTicks == 0 && targetTicks == 0)
        {
            return BridgeMessage.Response(cmd.RequestId, cmd.Action, true, new
            {
                legsPlaced = 0,
                message = "Both legs disabled (0) — nothing to place.",
            });
        }

        var tickSize = ctx.Security!.TickSize;
        if (tickSize <= 0m)
            return BridgeMessage.Failure(cmd.RequestId, cmd.Action, "NO_MARKET_DATA", $"Invalid tick size for {name} — cannot compute a bracket.");

        var quantity = Math.Abs(position.Volume);
        if (quantity <= 0m)
            return BridgeMessage.Failure(cmd.RequestId, cmd.Action, "NO_POSITION", "Position quantity is zero — nothing to protect.");

        var isLong = position.Volume > 0m;
        var averagePrice = position.AveragePrice;
        var exit = isLong ? OrderDirections.Sell : OrderDirections.Buy;
        var market = ContextResolver.MarketPrice(ctx.Security);

        var stopPrice = RoundToTick(isLong ? averagePrice - stopTicks * tickSize : averagePrice + stopTicks * tickSize, tickSize);
        var targetPrice = RoundToTick(isLong ? averagePrice + targetTicks * tickSize : averagePrice - targetTicks * tickSize, tickSize);

        // A leg left by a position of the OPPOSITE side is the only order this macro can produce
        // that is able to OPEN a trade: the stop of a long is a Sell, and on the short that followed
        // it ADDS to the position instead of leaving it. It happens as soon as the trader reverses
        // at market without going through the Reverse key, which cancels first. Only OUR OWN orders
        // are cancelled — never anything the trader placed himself.
        CancelStaleBracketLegs(cmd, ctx, isLong);

        var exits = _resolver.FindActiveOrders(ctx.Portfolio, ctx.Security)
            .Where(o => o.Direction == exit)
            .ToList();

        var existingStops = exits.Where(o => o.Type is OrderTypes.Stop or OrderTypes.StopLimit).ToList();
        var existingTargets = exits.Where(o => o.Type == OrderTypes.Limit).ToList();

        var ourStop = existingStops.FirstOrDefault(o => string.Equals(o.Comment, BracketStopComment, StringComparison.OrdinalIgnoreCase));
        var ourTarget = existingTargets.FirstOrDefault(o => string.Equals(o.Comment, BracketTargetComment, StringComparison.OrdinalIgnoreCase));

        // Reuse the OCO group of a leg this macro already owns, so the pair survives a scale-in:
        // a modification cannot rewrite an OCO id, and creating the second leg in a group of its own
        // would leave the two unlinked. A fresh group when we own neither.
        var oco = !string.IsNullOrEmpty(ourStop?.OCOGroup) ? ourStop!.OCOGroup
                : !string.IsNullOrEmpty(ourTarget?.OCOGroup) ? ourTarget!.OCOGroup
                : Guid.NewGuid().ToString("N");

        var stopOutcome = "disabled";
        var targetOutcome = "disabled";
        var placed = 0;
        var refused = 0;

        if (stopTicks > 0)
        {
            // A stop for a long sits BELOW the market, a stop for a short ABOVE it. Past that line
            // the order is marketable: it would not protect the trade, it would close it on the spot
            // at whatever price is there.
            var wrongSide = market > 0m && (isLong ? stopPrice >= market : stopPrice <= market);

            if (wrongSide)
            {
                stopOutcome = "refused:pastMarket";
                refused++;
                SdLog.EventWarn("Protection",
                    "[REQ:{0}] Stop loss NOT placed on {1}: {2} is already past the market ({3}) — the trade is beyond its stop",
                    cmd.RequestId, name, stopPrice, market);
            }
            else if (ourStop != null)
            {
                var failure = Modify(cmd, ourStop, updated =>
                {
                    var limitOffset = updated.Type == OrderTypes.StopLimit ? updated.Price - updated.TriggerPrice : 0m;
                    updated.TriggerPrice = stopPrice;
                    // Resized explicitly rather than left alone: a scale-in grew the position, and a
                    // stop still sized for the first entry protects only part of it.
                    updated.QuantityToFill = quantity;
                    if (updated.Type == OrderTypes.StopLimit) updated.Price = stopPrice + limitOffset;
                });
                if (failure != null) return failure;
                stopOutcome = "modified";
                placed++;
            }
            else if (existingStops.Count > 0)
            {
                // Someone else already protects this position — a manual stop, a break-even. That it
                // is protected is the whole point: a second stop would exit twice the size and open
                // the reverse trade.
                stopOutcome = "kept:foreign";
            }
            else
            {
                var order = NewOrder(ctx, exit, OrderTypes.Stop, quantity, BracketStopComment);
                order.TriggerPrice = stopPrice;
                order.OCOGroup = oco;
                var failure = Submit(cmd, order);
                if (failure != null) return failure;
                stopOutcome = "created";
                placed++;
            }
        }

        if (targetTicks > 0)
        {
            // Mirror of the stop test. A take profit the market has already crossed is a marketable
            // limit: submitting it would close the position immediately, which is exactly the
            // surprise this macro must never produce.
            var wrongSide = market > 0m && (isLong ? targetPrice <= market : targetPrice >= market);

            if (wrongSide)
            {
                targetOutcome = "refused:pastMarket";
                refused++;
                SdLog.EventWarn("Protection",
                    "[REQ:{0}] Take profit NOT placed on {1}: {2} is already past the market ({3}) — it would fill at once",
                    cmd.RequestId, name, targetPrice, market);
            }
            else if (ourTarget != null)
            {
                var failure = Modify(cmd, ourTarget, updated =>
                {
                    updated.Price = targetPrice;
                    updated.QuantityToFill = quantity;
                });
                if (failure != null) return failure;
                targetOutcome = "modified";
                placed++;
            }
            else if (existingTargets.Count > 0)
            {
                targetOutcome = "kept:foreign";
            }
            else
            {
                var order = NewOrder(ctx, exit, OrderTypes.Limit, quantity, BracketTargetComment);
                order.Price = targetPrice;
                order.OCOGroup = oco;
                var failure = Submit(cmd, order);
                if (failure != null) return failure;
                targetOutcome = "created";
                placed++;
            }
        }

        // Nothing placed and the market is the reason: a real refusal, and the deck must show it. A
        // leg left to a protection the trader already had is NOT a failure — the position is
        // covered, which is all the macro was ever asked for.
        if (placed == 0 && refused > 0)
        {
            return BridgeMessage.Failure(cmd.RequestId, cmd.Action, "INVALID_STOP_PRICE",
                $"Bracket not placed on {name}: the market ({market}) is already past the levels computed from the average price ({averagePrice}).");
        }

        SdLog.Event("Protection",
            "[REQ:{0}] Bracket on {1} {2} x{3} @ {4} — stop={5} ({6}), target={7} ({8}), oco={9}",
            cmd.RequestId, name, isLong ? "Long" : "Short", quantity, averagePrice,
            stopTicks > 0 ? stopPrice.ToString(CultureInfo.InvariantCulture) : "-", stopOutcome,
            targetTicks > 0 ? targetPrice.ToString(CultureInfo.InvariantCulture) : "-", targetOutcome, oco);

        return BridgeMessage.Response(cmd.RequestId, cmd.Action, true, new
        {
            avgPrice = averagePrice,
            quantity = (int)quantity,
            direction = isLong ? "Long" : "Short",
            stopPrice = stopTicks > 0 ? stopPrice : 0m,
            targetPrice = targetTicks > 0 ? targetPrice : 0m,
            stopLossTicks = stopTicks,
            takeProfitTicks = targetTicks,
            stopOutcome,
            targetOutcome,
            legsPlaced = placed,
            oco,
            message = $"Bracket on {name}: stop {stopOutcome}, target {targetOutcome}",
        });
    }

    /// <summary>
    /// Cancels the bracket legs THIS macro left behind that would now grow the position instead of
    /// closing it — what remains of a bracket after the position flipped side.
    ///
    /// Scoped to our own two comments on purpose. The macro may clean up after itself; it may never
    /// cancel a protection the trader placed. And a failure here must not stop the new bracket from
    /// going out — an orphan order is bad, an unprotected position is worse.
    /// </summary>
    private void CancelStaleBracketLegs(BridgeMessage cmd, TradingContext ctx, bool isLong)
    {
        try
        {
            var grows = isLong ? OrderDirections.Buy : OrderDirections.Sell;

            var stale = _resolver.FindActiveOrders(ctx.Portfolio, ctx.Security)
                .Where(o => o.Direction == grows)
                .Where(o => string.Equals(o.Comment, BracketStopComment, StringComparison.OrdinalIgnoreCase)
                         || string.Equals(o.Comment, BracketTargetComment, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (stale.Count == 0) return;

            Cancel(cmd, stale);
            SdLog.EventWarn("Protection",
                "[REQ:{0}] {1} stale bracket leg(s) cancelled on {2}: left by a position of the opposite side, they would have ADDED to the current {3}",
                cmd.RequestId, stale.Count, ContextResolver.InstrumentName(ctx.Security), isLong ? "Long" : "Short");
        }
        catch (Exception ex)
        {
            SdLog.Fail("Protection", ex, "[REQ:{0}] Could not cancel the stale bracket legs on {1}",
                cmd.RequestId, ContextResolver.InstrumentName(ctx.Security));
        }
    }
```

Ajouter en tête de `OrderEngine.cs` : `using System.Globalization;` et `using System.Linq;`.

- [ ] **Étape 4 : écrire les deux déplacements**

```csharp
    /// <summary>
    /// Shifts EVERY working stop by the same delta.
    ///
    /// Picking one of them was arbitrary on a scaled position and left part of it unprotected at the
    /// old level. Positive deltaTicks tightens the stop — moves it toward the market — negative
    /// gives the trade more room, in both directions.
    /// </summary>
    public BridgeMessage MoveStop(BridgeMessage cmd) => Move(cmd, stops: true);

    /// <summary>Mirror of MoveStop for the targets, and for the same reason.</summary>
    public BridgeMessage MoveTarget(BridgeMessage cmd) => Move(cmd, stops: false);

    private BridgeMessage Move(BridgeMessage cmd, bool stops)
    {
        var ctx = Resolve(cmd);
        if (ctx.Error != null) return ctx.Error;

        var name = ContextResolver.InstrumentName(ctx.Security);
        var position = _resolver.FindPosition(ctx.Portfolio, ctx.Security);
        if (position == null)
            return BridgeMessage.Failure(cmd.RequestId, cmd.Action, "NO_POSITION", "No open position.");

        var orders = stops
            ? _resolver.FindStopOrders(ctx.Portfolio, ctx.Security)
            : _resolver.FindTargetOrders(ctx.Portfolio, ctx.Security);

        if (orders.Count == 0)
        {
            return BridgeMessage.Failure(cmd.RequestId, cmd.Action,
                stops ? "NO_STOP_ORDER" : "NO_TARGET_ORDER",
                stops ? "No active stop order to move." : "No active target order to move.");
        }

        var deltaTicks = cmd.GetInt("deltaTicks") ?? 0;
        if (deltaTicks == 0)
            return BridgeMessage.Failure(cmd.RequestId, cmd.Action, "INVALID_PAYLOAD", "deltaTicks must be non-zero.");

        var tickSize = ctx.Security!.TickSize;
        if (tickSize <= 0m)
            return BridgeMessage.Failure(cmd.RequestId, cmd.Action, "NO_MARKET_DATA", $"Invalid tick size for {name}.");

        var isLong = position.Volume > 0m;
        var moved = 0;
        decimal firstOld = 0m, firstNew = 0m;

        foreach (var order in orders)
        {
            var current = stops ? order.TriggerPrice : order.Price;
            var next = RoundToTick(isLong ? current + deltaTicks * tickSize : current - deltaTicks * tickSize, tickSize);

            // A price at or below zero is not a move, it is a corrupt order. Skipped rather than
            // sent: the venue would refuse it with a message nobody can act on.
            if (next <= 0m) continue;

            var failure = Modify(cmd, order, updated =>
            {
                if (stops)
                {
                    var limitOffset = updated.Type == OrderTypes.StopLimit ? updated.Price - updated.TriggerPrice : 0m;
                    updated.TriggerPrice = next;
                    if (updated.Type == OrderTypes.StopLimit) updated.Price = next + limitOffset;
                }
                else
                {
                    updated.Price = next;
                }
            });
            if (failure != null) return failure;

            if (moved == 0) { firstOld = current; firstNew = next; }
            moved++;
        }

        if (moved == 0)
        {
            return BridgeMessage.Failure(cmd.RequestId, cmd.Action, "INVALID_STOP_PRICE",
                stops ? "The requested move would put the stop at an invalid price."
                      : "The requested move would put the target at an invalid price.");
        }

        SdLog.Event("Protection", "[REQ:{0}] {1} {2}(s) moved {3} tick(s): {4} → {5}",
            cmd.RequestId, moved, stops ? "stop" : "target", deltaTicks, firstOld, firstNew);

        return stops
            ? BridgeMessage.Response(cmd.RequestId, cmd.Action, true, new
            {
                oldPrice = firstOld, newPrice = firstNew, deltaTicks, stopsModified = moved,
                message = moved > 1 ? $"{moved} stops moved by {deltaTicks} tick(s)" : $"Stop moved from {firstOld} to {firstNew}",
            })
            : BridgeMessage.Response(cmd.RequestId, cmd.Action, true, new
            {
                oldPrice = firstOld, newPrice = firstNew, deltaTicks, targetsModified = moved,
                message = moved > 1 ? $"{moved} targets moved by {deltaTicks} tick(s)" : $"Target moved from {firstOld} to {firstNew}",
            });
    }
```

- [ ] **Étape 5 : ouvrir les quatre dernières branches**

Dans `CommandDispatcher.Dispatch` :

```csharp
                "breakeven" => _engine.BreakEven(cmd),
                // No key produces this action: the host's Auto TP/SL macro is its only emitter, and
                // only once the position actually exists.
                "attachBracket" => _engine.AttachBracket(cmd),
                "moveStop" => _engine.MoveStop(cmd),
                "moveTarget" => _engine.MoveTarget(cmd),
```

Les treize branches sont désormais présentes : le `default` ne doit plus être atteint que par une
action inconnue du protocole.

- [ ] **Étape 6 : compiler**

```bash
dotnet build src/Atas.Strategy.TradeDeck/Atas.Strategy.TradeDeck.csproj -c Release
```

Attendu : `Build succeeded`.

- [ ] **Étape 7 : vérifier — recette étape 6, seconde moitié**

Sur compte Sim :

| Geste | Attendu |
|---|---|
| Long 2 **en profit**, touche Break-even | un stop apparaît au prix moyen, la touche affiche le stop |
| Long 2 **en perte**, touche Break-even | refus `INVALID_STOP_PRICE`, **aucun ordre** |
| Long 2 avec stop, touche Break-even | le stop existant est **déplacé**, aucun second stop |
| Long 1 puis renfort à 2, `attachBracket` rejoué | les **deux** jambes suivent le nouveau prix moyen et repassent à la quantité 2, `stopOutcome: "modified"` |
| Position avec un stop **posé à la main**, `attachBracket` | `stopOutcome: "kept:foreign"`, le stop du trader **intact** |
| Take profit exécuté | le stop de la même paire **disparaît tout seul** (OCO) — le contrôle le plus important de la tâche |
| Long 2 avec **deux** stops, touche Resserrer le stop | les **deux** stops bougent du même delta |
| Position sans stop, touche Déplacer le stop | refus `NO_STOP_ORDER` |

**Le contrôle OCO se fait en premier après le bracket**, avant les déplacements : sans lui, un take
profit exécuté laisse un stop actif sur une position à plat, et ce stop est une *entrée* dès qu'il
se déclenche. Si les deux jambes ne sont pas liées, vérifier que le courtier ATAS supporte l'OCO
serveur — **jamais via `ChartStrategy.Connector.IsSupportedServerOCO`, cette propriété est toujours
nulle** (tâche 3). Le connecteur à interroger est celui qui porte le portefeuille résolu, obtenu par
`_resolver.ConnectorFor(ctx.Portfolio)` ; si cette propriété n'existe pas sur son type (à vérifier par
réflexion à ce moment-là, elle n'est pas listée dans « Surface d'API ATAS utilisée par ce plan »),
traiter l'absence comme « support inconnu » et **s'arrêter et le signaler** plutôt que de livrer un
bracket dont une jambe survit.

- [ ] **Étape 8 : committer**

```bash
git add src/Atas.Strategy.TradeDeck/ binaries/
git commit -m "Protections depuis ATAS : break-even, bracket OCO, deplacement des stops et des targets"
```

---

## Tâche 8 : `GuardEnforcer`

`GuardEnforcer` est **dans** le périmètre, pas un supplément. Il annule les ordres passés hors du
deck — DOM, Chart Trader — qui ouvriraient ou agrandiraient une position pendant que la macro refuse
les entrées. Sans lui, la macro se contourne en tapant l'ordre à la main, et « chaque limite devient
décorative au moment exact où elle compte ».

Il ne réduit jamais une position et ne poste jamais d'ordre : le pire cas d'un faux positif est un
ordre annulé, **jamais un trader enfermé dans une position**.

**Compatible avec la règle « pas d'entrée avant une heure » sans code supplémentaire.** Cette classe
ne connaît **aucune** raison de refus en dur : elle reçoit `{blocked, reason, maxContracts}` de
`setGuardPolicy` et applique le même test générique — l'ordre agrandit-il l'exposition ? — quelle
que soit `reason`. `beforeSessionStart` emprunte donc exactement le même chemin que `dailyLoss`.
**L'heure locale, l'armement et le compte à rebours sont une décision du bridge seul** : cette
classe ne lit jamais l'horloge.

⚠ **Cette classe dépend entièrement des événements du connecteur, qui n'existent plus en mode
dégradé** (registre introuvable — voir la tâche 3 et la note de la tâche 4, étape 4). Sans connecteur,
`OrderMonitor` ne reçoit plus `OrderChanged` / `NewOrders`, donc `GuardEnforcer.Inspect` n'est plus
appelé et un ordre passé hors du deck n'est plus surveillé du tout. Ce plan **ne spécifie pas** de
sondage périodique de `TradingManager.Orders` pour compenser — ce serait un mécanisme distinct, non
mesuré (voir la tâche 9). L'étape 5 doit donc **constater et journaliser** ce trou plutôt que de le
découvrir en production : le rapport de tâche doit dire explicitement si le mode dégradé a été
recréé et testé, et si oui, confirmer que la macro de sécurité **côté bridge** (qui continue de
refuser les commandes venues du deck, elle, quel que soit le mode) reste le seul filet dans ce cas.

**Fichiers :**
- Créer : `src/Atas.Strategy.TradeDeck/GuardEnforcer.cs`
- Modifier : `src/Atas.Strategy.TradeDeck/OrderMonitor.cs`
- Modifier : `src/Atas.Strategy.TradeDeck/TradeDeckStrategy.cs`

**Interfaces :**
- Consomme : `ContextResolver.FindPosition/FindPortfolio`, `OrderEngine.IsOurs`, `BridgeClient`.
- Produit : `GuardEnforcer` — `GuardEnforcer(ContextResolver, BridgeClient, OrderEngine, IOrderGateway)`,
  `void UpdatePolicy(bool blocked, string reason, int maxContracts)`,
  `bool Inspect(Order order)`, `int MaxContracts { get; }`, `bool IsBlocking { get; }`,
  `void SetTrackedAccount(string account)`,
  `const string OutcomeBypassed = "bypassed"`, `const string OutcomeStopped = "stopped"`.

- [ ] **Étape 1 : écrire l'applicateur**

Créer `src/Atas.Strategy.TradeDeck/GuardEnforcer.cs` :

```csharp
using System;
using System.Collections.Generic;

using ATAS.DataFeedsCore;
using ATAS.Indicators;

namespace TradeDeck.Atas.Strategy;

/// <summary>
/// Applies the bridge's safety rules to orders the bridge never sees.
///
/// THE HOLE THIS CLOSES. The safety macro lives in the bridge, which only knows about commands
/// coming from the deck. An order typed into the DOM or the Chart Trader reaches the market without
/// ever crossing it — so a trader who has just been refused an entry can simply place it himself,
/// and every limit becomes decorative at the exact moment it matters. This strategy runs INSIDE the
/// platform and is subscribed to the connector, so it sees every order on the account whatever its
/// origin. It is the only place the hole can be closed.
///
/// WHAT IT NEVER DOES. It never cancels an order that reduces exposure — a manual stop, a target, a
/// scale-out, a panic exit. Trapping the trader inside a position is the one outcome none of these
/// rules may ever produce, and it is why the test below is about GROWTH and not about "is this an
/// entry". It also never submits an order of its own: cancelling cannot create a position, so the
/// worst case of a false positive is a cancelled order, not a trade.
///
/// WHAT IT CANNOT DO. A market order on a liquid future often fills before the platform reports it,
/// and a filled order cannot be cancelled. That case is detected and reported, not prevented. And
/// anyone willing to stop this strategy, or trade from the broker's own platform, is past what any
/// of this can reach. The point is to turn a two-click impulse into a deliberate act of dismantling
/// one's own safety net.
///
/// IT KNOWS NO REASON BY NAME. `reason` travels through untouched — dailyLoss, tradeLimit,
/// beforeSessionStart or anything the bridge adds later. The clock, the arming and the countdown are
/// the bridge's business alone; this class never reads the time.
/// </summary>
internal sealed class GuardEnforcer
{
    private readonly ContextResolver _resolver;
    private readonly BridgeClient _bridge;
    private readonly OrderEngine _engine;
    private readonly IOrderGateway _gateway;

    private readonly object _lock = new();

    private bool _blocked;
    private string _reason = string.Empty;
    private int _maxContracts;
    private string _trackedAccount = string.Empty;

    /// <summary>Orders already acted upon, so a second update does not cancel twice.</summary>
    private readonly HashSet<string> _handled = new(StringComparer.Ordinal);

    /// <summary>
    /// Orders a cancel was requested for, waiting to find out whether it worked.
    /// Order id → violation reason.
    /// </summary>
    private readonly Dictionary<string, string> _pending = new(StringComparer.Ordinal);

    /// <summary>The order got through: it filled despite the refusal.</summary>
    public const string OutcomeBypassed = "bypassed";

    /// <summary>The order was really stopped — cancelled or rejected before filling.</summary>
    public const string OutcomeStopped = "stopped";

    public int MaxContracts { get { lock (_lock) return _maxContracts; } }
    public bool IsBlocking { get { lock (_lock) return _blocked; } }

    public GuardEnforcer(ContextResolver resolver, BridgeClient bridge, OrderEngine engine,
                         IOrderGateway gateway)
    {
        _resolver = resolver;
        _bridge = bridge;
        _engine = engine;
        _gateway = gateway;
    }

    public void SetTrackedAccount(string account)
    {
        lock (_lock) _trackedAccount = account ?? string.Empty;
    }

    /// <summary>Adopts the policy the bridge publishes whenever it changes.</summary>
    public void UpdatePolicy(bool blocked, string? reason, int maxContracts)
    {
        lock (_lock)
        {
            var changed = _blocked != blocked
                       || !string.Equals(_reason, reason ?? string.Empty, StringComparison.Ordinal)
                       || _maxContracts != maxContracts;

            _blocked = blocked;
            _reason = reason ?? string.Empty;
            _maxContracts = maxContracts;

            // Logged only on change: the bridge pushes this on connection and then only when it
            // moves, but the discipline belongs on both sides of the wire.
            if (!changed) return;
        }

        SdLog.Event("Safety", "Guard policy adopted — blocked={0} reason={1} maxContracts={2}",
            blocked, string.IsNullOrEmpty(reason) ? "-" : reason, maxContracts);
    }

    /// <summary>
    /// Called for every order update on the connector. Returns true when a cancel was requested, so
    /// the caller can refresh the deck.
    /// </summary>
    public bool Inspect(Order? order)
    {
        if (order == null) return false;

        bool blocked;
        string reason;
        int cap;
        string account;
        lock (_lock)
        {
            blocked = _blocked;
            reason = _reason;
            cap = _maxContracts;
            account = _trackedAccount;
        }

        // BEFORE every early return below: an order we already acted on must be followed to its
        // conclusion whatever the policy has become since. Clearing the block, or the order ceasing
        // to be cancellable — which is precisely what happens when it fills — would otherwise lose
        // the only update that says how the story ended.
        ResolveOutcome(order);

        if (!blocked && cap <= 0) return false;

        // SCOPED TO THE TRACKED ACCOUNT. Every safety rule of this protocol is scoped to one
        // account, and enforcing on a portfolio nobody designated is how, on 2026-08-21, a locked
        // macro coexisted with an hour of hand-placed orders.
        if (account.Length > 0 &&
            !string.Equals(order.Portfolio?.AccountID, account, StringComparison.OrdinalIgnoreCase))
            return false;

        // The bridge already ran every rule against this one before it was sent.
        if (_engine.IsOurs(order)) return false;

        if (!ContextResolver.IsActive(order)) return false;
        if (!GrowsExposure(order)) return false;

        string violation;
        if (blocked)
        {
            violation = string.IsNullOrEmpty(reason) ? "guardBlocked" : reason;
        }
        else
        {
            var held = HeldQuantity(order);
            var resulting = held + (int)order.Unfilled;
            if (resulting <= cap) return false;
            violation = "maxContracts";
        }

        var id = order.Id ?? string.Empty;
        lock (_lock)
        {
            if (id.Length > 0 && !_handled.Add(id)) return false;
        }

        return CancelAndReport(order, violation);
    }

    private bool CancelAndReport(Order order, string violation)
    {
        var instrument = ContextResolver.InstrumentName(order.Security);

        try
        {
            _gateway.Cancel(order);

            SdLog.EventWarn("Safety",
                "EXTERNAL ORDER CANCEL REQUESTED — {0} {1} qty={2} on {3} (comment='{4}', state={5}) refused by the safety macro: {6}",
                order.Direction, order.Type, order.QuantityToFill, instrument,
                order.Comment ?? string.Empty, order.State, violation);
        }
        catch (Exception ex)
        {
            // A cancel that throws is the one case settled on the spot: the order is alive and the
            // macro did not stop it.
            SdLog.Fail("Safety", ex, "Could not cancel external order on {0}", instrument);
            Report(order, violation, OutcomeBypassed, ex.Message);
            return false;
        }

        // DELIBERATELY NOT REPORTED AS CANCELLED HERE. Cancelling is asynchronous: returning without
        // throwing says only that the request left. On 2026-08-12, thirty of thirty-five such
        // requests came back "order is complete" because the market order had already filled — and
        // every one of them had been journalled as cancelled:true, so Bitlearn read the day as 100%
        // rule compliance while every trade carried a bypass. The verdict is whatever state the
        // order reaches NEXT, and ResolveOutcome sends it.
        lock (_lock) _pending[order.Id ?? string.Empty] = violation;
        return true;
    }

    /// <summary>
    /// Reports how a cancelled-at order actually ended, once the platform says so.
    ///
    /// ATAS only has four order states, so the conclusion is read from what actually traded rather
    /// than from a state name: anything filled means the trader got through — the bypass worked.
    /// </summary>
    private void ResolveOutcome(Order order)
    {
        var id = order.Id ?? string.Empty;
        if (id.Length == 0) return;

        string violation;
        string outcome;
        lock (_lock)
        {
            if (!_pending.TryGetValue(id, out violation!)) return;

            var terminal = TerminalOutcome(order);
            if (terminal == null) return;

            outcome = terminal;
            _pending.Remove(id);
        }

        var instrument = ContextResolver.InstrumentName(order.Security);
        if (outcome == OutcomeStopped)
        {
            SdLog.EventWarn("Safety", "EXTERNAL ORDER STOPPED — {0} {1} qty={2} on {3}: {4} ({5})",
                order.Direction, order.Type, order.QuantityToFill, instrument, violation, order.State);
        }
        else
        {
            // The line that matters on a bad day: the rule refused AND the order got through.
            SdLog.EventWarn("Safety", "GUARD BYPASSED — {0} {1} qty={2} on {3} FILLED despite {4}",
                order.Direction, order.Type, order.QuantityToFill, instrument, violation);
        }

        Report(order, violation, outcome, string.Empty);
    }

    /// <summary>Null while the order can still change its mind.</summary>
    private static string? TerminalOutcome(Order order) => order.Status() switch
    {
        // Anything that traded is a bypass, even partially and even if the rest was then cancelled:
        // the trader took exposure the rule was refusing, which is the whole point of the report.
        OrderStatus.Filled or OrderStatus.PartlyFilled => OutcomeBypassed,
        OrderStatus.Canceled => OutcomeStopped,
        // Placed and None are not a conclusion yet.
        _ => null,
    };

    private void Report(Order order, string violation, string outcome, string error)
    {
        try
        {
            var payload = new Dictionary<string, object?>
            {
                ["violation"] = violation,
                // Kept for readers that predate `outcome`, and now honest: true only when the order
                // was really stopped, never merely asked to stop.
                ["cancelled"] = outcome == OutcomeStopped,
                ["outcome"] = outcome,
                ["orderId"] = order.Id ?? string.Empty,
                ["orderAction"] = order.Direction.ToString(),
                ["orderType"] = order.Type.ToString(),
                ["quantity"] = (int)order.QuantityToFill,
                ["name"] = order.Comment ?? string.Empty,
                ["instrument"] = ContextResolver.InstrumentName(order.Security),
            };
            if (!string.IsNullOrEmpty(error)) payload["error"] = error;

            // Fire-and-forget: this runs inside the connector's event pipeline and cannot wait.
            _ = _bridge.SendAsync(BridgeMessage.Notify("guardViolation", payload));
        }
        catch (Exception ex)
        {
            SdLog.Fail("Safety", ex, "Could not report guard violation");
        }
    }

    /// <summary>
    /// Would this order make the position BIGGER?
    ///
    /// The whole safety of this class rests on this test. A manual stop on a long is a Sell and a
    /// manual stop on a short is a Buy: both reduce, both must go through untouched even while every
    /// entry is refused.
    /// </summary>
    private bool GrowsExposure(Order order)
    {
        var portfolio = order.Portfolio;
        var position = _resolver.FindPosition(portfolio, order.Security);

        if (position == null) return true;               // flat: any order opens
        if (position.Volume > 0m) return order.Direction == OrderDirections.Buy;
        return order.Direction == OrderDirections.Sell;
    }

    private int HeldQuantity(Order order)
    {
        var position = _resolver.FindPosition(order.Portfolio, order.Security);
        return position == null ? 0 : (int)Math.Abs(position.Volume);
    }
}
```

- [ ] **Étape 2 : faire passer chaque ordre par l'applicateur**

Dans `OrderMonitor.cs`, ajouter le champ et le paramètre :

```csharp
    private readonly GuardEnforcer? _enforcer;

    public OrderMonitor(BridgeClient bridge, GuardEnforcer? enforcer = null)
    {
        _bridge = bridge;
        _enforcer = enforcer;
    }
```

et brancher l'inspection **en tête** de `OnOrderChanged`, juste après le test de nullité :

```csharp
            // Enforcement first, and before any other consideration: this is the only moment an
            // order placed outside the deck can still be stopped. Every millisecond spent elsewhere
            // is a millisecond it may fill in.
            if (_enforcer != null && _enforcer.Inspect(order))
            {
                StateChanged?.Invoke();
                return;
            }
```

Ajouter aussi l'abonnement aux ordres **nouveaux**, qui est le premier moment où un ordre extérieur
apparaît — dans `AttachOne()` (tâche 4) :

```csharp
            connector.NewOrders += OnNewOrders;
```

dans `DetachOne()` : `connector.NewOrders -= OnNewOrders;`

et le gestionnaire :

```csharp
    private void OnNewOrders(IDataFeedConnector connector, IEnumerable<Order> orders) =>
        Safely("Safety", () =>
        {
            if (_enforcer == null) return;
            // The earliest sighting of an order placed outside the deck. OrderChanged follows, but
            // on a market order the two may be a fill apart.
            foreach (var order in orders)
            {
                if (_enforcer.Inspect(order)) StateChanged?.Invoke();
            }
        });
```

- [ ] **Étape 3 : adopter `setGuardPolicy` dans la stratégie**

Dans `TradeDeckStrategy.cs`, ajouter le champ `private GuardEnforcer? _guard;`, le construire dans
`OnStarted()` **avant** le moniteur :

```csharp
            // Closes the hole the bridge cannot reach: orders placed straight into the platform
            // never cross it, so the safety macro has to be applied from inside.
            _guard = new GuardEnforcer(_resolver, _bridge, _engine, this);
            _monitor = new OrderMonitor(_bridge, _guard);
```

ajouter la branche dans le `switch` de `OnBridgeMessage`, avant `setAccount` :

```csharp
            "setGuardPolicy" => HandleGuardPolicy(message),
```

et la méthode :

```csharp
    /// <summary>
    /// Adopts the safety policy the bridge publishes. Handled here rather than in the dispatcher
    /// because it sends no order and touches no position: it only tells the enforcer what to refuse
    /// when an order arrives from outside the deck.
    /// </summary>
    private BridgeMessage HandleGuardPolicy(BridgeMessage message)
    {
        if (_guard == null || _publisher == null)
            return BridgeMessage.Failure(message.RequestId, message.Action, "CONTEXT_MISSING", "Guard enforcer is not initialized.");

        var blocked = message.GetBool("blocked");
        var reason = message.GetString("reason");
        var maxContracts = message.GetInt("maxContracts") ?? 0;

        // A policy that refuses something, adopted while nobody has told us which account to watch,
        // is the shape of the 2026-08-21 bypass: armed, displayed red on the deck, and pointed at an
        // account the trader was not using. The policy is still applied — half a guard beats none —
        // but it never passes in silence again.
        if ((blocked || maxContracts > 0) && !_accountConfirmedByBridge)
        {
            SdLog.EventWarn("Safety",
                "SECURITY: adopting a blocking policy (blocked={0} reason={1} maxContracts={2}) while the bridge has NOT named the account — "
                + "enforcement is running on '{3}', picked by default at startup. Orders on any other account are NOT being watched.",
                blocked, string.IsNullOrEmpty(reason) ? "-" : reason, maxContracts,
                string.IsNullOrEmpty(_publisher.TrackedAccount) ? "(none)" : _publisher.TrackedAccount);
        }

        _guard.SetTrackedAccount(_publisher.TrackedAccount);
        _guard.UpdatePolicy(blocked, reason, maxContracts);

        return BridgeMessage.Response(message.RequestId, message.Action, true, new { blocked, maxContracts });
    }
```

Enfin, dans `HandleTracking`, après `_publisher.UpdateTracking(...)` :

```csharp
        // The enforcer follows the tracked account, always. A guard watching an account nobody
        // trades is the failure mode this line exists to prevent.
        _guard?.SetTrackedAccount(_publisher.TrackedAccount);
```

- [ ] **Étape 4 : compiler**

```bash
dotnet build src/Atas.Strategy.TradeDeck/Atas.Strategy.TradeDeck.csproj -c Release
```

Attendu : `Build succeeded`.

- [ ] **Étape 5 : vérifier — recette étape 7**

Le cas le plus simple à rejouer à la demande est **`beforeSessionStart`** : régler l'heure quelques
minutes dans le futur arme le refus sans attendre une vraie perte de journée, contrairement à
`dailyLoss` ou `tradeLimit` qu'il faut provoquer par des trades réels.

1. Sur la touche Macro de sécurité, activer « pas d'entrée avant l'heure » avec une heure **cinq
   minutes dans le futur**, puis armer la macro.
2. Vérifier que `addon-*.log` porte
   `Safety | Guard policy adopted — blocked=True reason=beforeSessionStart maxContracts=0`.
3. **Taper un ordre d'achat au marché directement dans le DOM d'ATAS**, sur le compte suivi.

Attendu :
- l'ordre est **annulé** (ou, s'il a rempli avant, remonté comme contourné) ;
- `addon-*.log` : `Safety | EXTERNAL ORDER CANCEL REQUESTED — Buy Market qty=1 … : beforeSessionStart`
  puis `Safety | EXTERNAL ORDER STOPPED — …` ;
- le journal du bridge : `GUARD VIOLATION reported by NT8: {...}` ;
- le deck en est informé.

Puis les deux contrôles qui garantissent qu'on n'enferme personne :
4. Ouvrir une position long à la main **avant** l'heure, puis **poser un stop à la main** :
   attendu — le stop **passe intact**, il réduit l'exposition.
5. Aplatir la position à la main : attendu — **rien n'est annulé**.

Si l'un de ces deux derniers contrôles échoue, `GrowsExposure` est faux : ne pas livrer.

6. **Contrôle du trou en mode dégradé.** Provoquer artificiellement un registre introuvable (voir la
   tâche 3, étape 6 — par exemple en coupant le `IsLocationProviderSet` via une sonde, ou plus
   simplement en constatant l'entrée en mode dégradé si elle se produit d'elle-même sur ce poste),
   puis rejouer l'étape 3 ci-dessus (ordre tapé dans le DOM, macro armée). Attendu, **et c'est un
   attendu négatif, pas un échec** : l'ordre **n'est pas annulé** et aucune ligne `Safety | EXTERNAL
   ORDER …` n'apparaît — conforme à la note de la tâche 4, étape 4. Noter le résultat dans le rapport
   de tâche : c'est la preuve que la dégradation est silencieuse pour ce garde-fou précis, information
   que le trader doit connaître avant de compter dessus en mode dégradé.

- [ ] **Étape 6 : committer**

```bash
git add src/Atas.Strategy.TradeDeck/ binaries/
git commit -m "Appliquer la macro de securite aux ordres passes hors du deck sous ATAS"
```

---

## Tâche 9 : le journal des exécutions et son scellement

Le journal **ne traverse pas le bridge**. `ExecutionRecorder` écrit une ligne JSON par exécution dans
`%APPDATA%\StreamDeckTrader\journal\` ; le deck-host lit, scelle la chaîne et téléverse. **Le chemin
et le format sont une interface, pas un choix** — le deck-host reste intact.

Source des exécutions : **`NewMyTrades` sur CHAQUE connecteur agrégé par le registre** (tâche 3), et
non `ChartStrategy.OnNewMyTrade`, et jamais un connecteur unique — les comptes sont répartis sur
plusieurs connecteurs. La différence est la raison d'être du journal : au niveau du connecteur on
voit **toutes** les exécutions du compte, y compris celles passées au DOM ou au Chart Trader. C'est
ce que le fichier revendique — « le seul composant qui voit la vérité ».

⚠ **Trou assumé du mode dégradé, à vérifier à l'étape 5 plutôt qu'à supposer.** `NewMyTrades` est un
événement du **connecteur** ; en mode dégradé il n'y en a aucun. `ITradingManager.MyTrades` existe
par réflexion (relevé dans le sondage du 24/08), mais son contenu et son comportement en direct
**n'ont pas été mesurés** — contrairement à `TradingManager.Orders`, confirmé peuplé sur l'essai
terrain. Si l'étape 5 montre qu'il reste vide ou ne se met pas à jour, le journal des exécutions
s'arrête simplement en mode dégradé (compte et instrument du graphique) plutôt que d'inventer un
mécanisme de sondage non vérifié ici : le signaler dans le rapport de tâche plutôt que de le
masquer.

Trois règles gouvernent cette classe, et **la première prime sur tout** : elle ne doit jamais pouvoir
casser le trading. Aucune exception ne s'en échappe, aucun appel réseau, aucune attente bloquante —
d'autant qu'ici on tourne dans le fil d'événements du connecteur.

**Fichiers :**
- Créer : `src/Atas.Strategy.TradeDeck/ExecutionRecorder.cs`
- Modifier : `src/Atas.Strategy.TradeDeck/OrderMonitor.cs`
- Modifier : `src/Atas.Strategy.TradeDeck/TradeDeckStrategy.cs`

**Interfaces :**
- Consomme : `TradeDeck.Journal.JournalSeal` (tâche 1), `ContextResolver.InstrumentName/PointValue`
  (tâche 3), `ITradingManager.IsStopLossOrder/IsTakeProfitOrder`.
- Produit : `ExecutionRecorder` — `ExecutionRecorder(IOrderGateway gateway, string? directory = null)`,
  `void Record(MyTrade trade)`, `void Dispose()`.

- [ ] **Étape 1 : écrire l'enregistreur**

Créer `src/Atas.Strategy.TradeDeck/ExecutionRecorder.cs` :

```csharp
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

using ATAS.DataFeedsCore;
using ATAS.Indicators;

using Newtonsoft.Json;

using TradeDeck.Journal;

namespace TradeDeck.Atas.Strategy;

/// <summary>
/// Records every fill to a local append-only spool, one JSON object per line.
///
/// This strategy is the only component that sees the truth: fills reported by the platform itself,
/// including orders placed outside the deck. The bridge and the host only ever see what passes
/// through them, which is a subset.
///
/// Three rules govern this class, and they outrank every other consideration:
///
///   1. It must never be able to break trading. It runs inside the connector's event pipeline: no
///      exception escapes, no network call, no blocking wait. Append a line, nothing else.
///   2. Point value and tick size are captured AT FILL TIME. The server can then compute P&amp;L
///      without an instrument table of its own — a fact that only exists at the instant of the
///      fill, which no amount of later work reconstructs.
///   3. The path, the file name and the field list are an INTERFACE, not a choice. The deck-host
///      reads this folder and seals nothing of its own here; changing any of the three silently
///      stops the journal from being uploaded.
///
/// Recording is local and unconditional; *uploading* is what the user opts into, per account, and
/// the host filters on that. Writing a line to the trader's own disk is not transmission, and a
/// spool that only started once the toggle was flipped would lose the session that made the trader
/// want the journal in the first place.
/// </summary>
internal sealed class ExecutionRecorder : IDisposable
{
    /// <summary>Kept beside the logs: one place to look, one place to clean up.</summary>
    private const string FolderName = "journal";

    /// <summary>
    /// Beyond this, a spool that was never uploaded is a disk leak rather than a safety net. The
    /// host removes what it has sent; this is the backstop for a host that never runs.
    /// </summary>
    private const int RetentionDays = 30;

    /// <summary>
    /// Beyond this many remembered ids the oldest are forgotten. A fill that arrived more than four
    /// thousand fills ago is not about to be re-raised.
    /// </summary>
    private const int MaxSeenExecutions = 4000;

    private readonly object _sync = new();
    private readonly string _directory;
    private readonly IOrderGateway _gateway;
    private readonly JournalSeal _seal;

    /// <summary>
    /// Executions already written, so the same fill is never journalled twice. A duplicated fill
    /// does not merely clutter the file: the Bitlearn journal would count the trade twice, and the
    /// P&amp;L it publishes would be twice what the trader made.
    /// </summary>
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    private readonly Queue<string> _seenOrder = new();

    private StreamWriter? _writer;
    private DateTime _openDate = DateTime.MinValue;
    private bool _disposed;
    private bool _sinkFailed;

    /// <summary>Says the DateTimeKind of the first fill once, so the timestamps can be audited.</summary>
    private bool _kindReported;

    public ExecutionRecorder(IOrderGateway gateway, string? directory = null)
    {
        _gateway = gateway;
        _directory = directory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "StreamDeckTrader", FolderName);

        _seal = new JournalSeal(_directory, (level, message, ex) =>
        {
            if (level == JournalSealLevel.Error) SdLog.Fail("Journal", ex, "{0}", message);
            else if (level == JournalSealLevel.Warn) SdLog.Warn("{0}: {1}", message, ex?.Message ?? "-");
            else SdLog.Info("{0}", message);
        });
    }

    /// <summary>
    /// Handles one fill.
    ///
    /// Swallows everything: an exception raised here would travel up the connector's event pipeline,
    /// and losing a journal line must never cost a trading session.
    /// </summary>
    public void Record(MyTrade? trade)
    {
        try
        {
            if (_disposed || trade == null) return;

            // A REPLAY FILL IS NOT A TRADE. ATAS's Replay mode walks historical data and raises real
            // MyTrade objects; journalling them would send fabricated trades to Bitlearn as if the
            // trader had taken them, inflating — or destroying — statistics nobody could later
            // explain. Skipped in silence at TRACE: a replay session raises thousands of these.
            if (trade.IsReplay())
            {
                SdLog.TraceEvent("Journal", "Replay fill ignored — {0}", trade.Id ?? "?");
                return;
            }

            if (AlreadyRecorded(trade)) return;

            var record = Describe(trade);
            Write(record);
        }
        catch (Exception ex)
        {
            SdLog.Fail("Journal", ex, "Execution not recorded");
        }
    }

    /// <summary>
    /// True when this exact fill has already been written.
    ///
    /// A fill without an id is recorded rather than dropped: an id is what makes deduplication
    /// possible, and its absence must not cost the line itself.
    /// </summary>
    private bool AlreadyRecorded(MyTrade trade)
    {
        var id = trade.Id;
        if (string.IsNullOrEmpty(id)) return false;

        lock (_sync)
        {
            if (!_seen.Add(id)) return true;
            _seenOrder.Enqueue(id);
            while (_seenOrder.Count > MaxSeenExecutions) _seen.Remove(_seenOrder.Dequeue());
            return false;
        }
    }

    /// <summary>
    /// Flattens a fill into the record the server expects.
    ///
    /// Every member is read defensively: a partially populated fill is rare but real during a
    /// disconnect, and a null reference here would be indistinguishable from a trading fault in the
    /// logs.
    /// </summary>
    private Dictionary<string, object> Describe(MyTrade trade)
    {
        var security = trade.Security;

        // Dictionary<string, object>, NOT <string, object?>: generics are invariant, and
        // JournalSeal.Stamp — the shared file — takes the non-nullable form. A null value still
        // goes in fine; only the type argument matters.
        var record = new Dictionary<string, object>
        {
            ["kind"] = "exec",
            ["execId"] = trade.Id ?? string.Empty,
            ["orderId"] = trade.OrderId ?? string.Empty,
            ["account"] = trade.AccountID ?? trade.Portfolio?.AccountID ?? string.Empty,
            // Buy means the fill took a long position, Sell a short. The server reads
            // "Long"/"Short", as the NinjaTrader add-on has always written it — mapping here keeps
            // one vocabulary across both platforms rather than teaching the server a second one.
            ["marketPosition"] = trade.OrderDirection == OrderDirections.Buy ? "Long" : "Short",
            ["price"] = trade.Price,
            ["quantity"] = trade.Volume,
            // Nullable, exactly as today: a commission the broker never reported must stay
            // distinguishable from a commission of zero.
            ["commission"] = trade.Commission,
            ["orderName"] = OrderName(trade),
        };

        // The trend verdict is deliberately OMITTED, never written as "neutral": "neutral" is a
        // verdict ("the market is going nowhere") and conflating it with "we could not tell" would
        // quietly count every fill as a trade taken with the trend. The trend macro is out of scope
        // for this port, so the server reads every ATAS fill as "unknown", which is exactly true.

        if (security != null)
        {
            record["instrument"] = ContextResolver.InstrumentName(security);
            record["tickSize"] = security.TickSize;

            // THE ONLY DERIVED FIELD, AND THE MOST SENSITIVE ONE. NinjaTrader hands PointValue over
            // ready-made; ATAS does not, so it comes from TickCost / TickSize. The server computes
            // the P&L of every journalled trade from this number and has no instrument table of its
            // own: wrong, it corrupts the statistics of a whole day without anything looking broken.
            //
            // Omitted rather than faked when it cannot be derived. An absent field is read as
            // unknown; a fabricated 1.0 would price a MNQ trade at a fiftieth of its value.
            var pointValue = ContextResolver.PointValue(security);
            if (pointValue > 0m) record["pointValue"] = pointValue;
            else SdLog.EventWarn("Journal", "No point value for {0} (tickSize={1}, tickCost={2}) — the field is omitted",
                ContextResolver.InstrumentName(security), security.TickSize, security.TickCost);
        }

        // Two timestamps, deliberately. `time` is when the fill happened; `recordedAtUtc` is when we
        // saw it. Keeping both lets the server reconcile ordering.
        //
        // `time` MUST carry its offset. An ISO-8601 string without a zone is read as the READER's
        // local time — a fill at 12:42 in New York became 12:42 UTC once ingested on a server in
        // another zone, four hours off. A DateTime whose Kind is Unspecified is treated as local,
        // as on the NinjaTrader side; see the verification step, which establishes what ATAS
        // actually hands over rather than assuming it.
        var when = trade.Time.Kind == DateTimeKind.Unspecified
            ? DateTime.SpecifyKind(trade.Time, DateTimeKind.Local)
            : trade.Time;

        if (!_kindReported)
        {
            _kindReported = true;
            SdLog.Event("Journal", "First fill timestamp: raw={0:o} kind={1} → recorded as {2}",
                trade.Time, trade.Time.Kind, new DateTimeOffset(when).ToString("o", CultureInfo.InvariantCulture));
        }

        record["time"] = new DateTimeOffset(when).ToString("o", CultureInfo.InvariantCulture);
        record["recordedAtUtc"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);

        // Trading day, computed exactly as the bridge computes it: DateTime.Now formatted as a local
        // date. The two run on the same machine, off the same clock, so they agree without any
        // message passing — and they MUST agree, because this is the window on which the guard
        // resets its daily loss and its trade count, and the window on which Bitlearn cuts the
        // session it scores.
        //
        // Deliberately NOT derived from the fill's own time: a fill can be reported minutes after
        // the fact, and around midnight that would file it under the wrong session.
        record["tradingDay"] = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        return record;
    }

    /// <summary>
    /// What the deck calls this order.
    ///
    /// It is what the server uses to tell an exit at the stop from an exit at the target, and to
    /// separate what the deck sent from what the trader placed by hand — the distinction the whole
    /// behavioural statistic rests on, since an order that never went through the deck is exactly
    /// what the guard rules cannot prevent, only report.
    ///
    /// ATAS has no Order.Name, so the comment carries it. When there is none — an order from the
    /// DOM, or from another strategy — the trading manager's own classification fills in, and the
    /// field is left empty rather than guessed.
    /// </summary>
    private string OrderName(MyTrade trade)
    {
        var order = trade.Order;
        if (order == null) return string.Empty;

        var comment = order.Comment;
        if (!string.IsNullOrWhiteSpace(comment)) return comment!;

        try
        {
            if (_gateway.IsStopLossOrder(order)) return OrderEngine.BracketStopComment;
            if (_gateway.IsTakeProfitOrder(order)) return OrderEngine.BracketTargetComment;
        }
        catch (Exception ex)
        {
            // Classification is a nicety; the fill is not. Never let it cost the line.
            SdLog.Warn("Order name not classified on a fill: {0}", ex.Message);
        }

        return string.Empty;
    }

    private void Write(Dictionary<string, object> record)
    {
        // Sealed BEFORE serialising: the signed line is the one that reaches the disk, leaving no
        // window during which the file holds an unsigned record.
        //
        // A null commission canonicalises to an empty string on both sides of the wire, which is
        // what keeps "the broker reported nothing" distinguishable from "the commission was zero".
        _seal.Stamp(record);

        var line = JsonConvert.SerializeObject(record);

        lock (_sync)
        {
            var writer = EnsureWriter();
            writer?.WriteLine(line);
        }
    }

    /// <summary>
    /// Opens today's spool, rolling at midnight. Returns null when the sink is unusable — a
    /// read-only profile, a full disk — and says so once instead of on every fill.
    /// </summary>
    private StreamWriter? EnsureWriter()
    {
        var today = DateTime.UtcNow.Date;

        if (_writer != null && today != _openDate) CloseWriter();
        if (_writer != null) return _writer;
        if (_sinkFailed) return null;

        try
        {
            Directory.CreateDirectory(_directory);
            _openDate = today;

            var path = Path.Combine(_directory,
                string.Format(CultureInfo.InvariantCulture, "executions-{0:yyyy-MM-dd}.ndjson", today));

            // FileShare.ReadWrite so the host can upload what is already written while this process
            // keeps appending — the two never coordinate, and must not have to.
            var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);

            // NO BOM, unlike the log files. This one is parsed line by line by a program, and a
            // byte-order mark would sit in front of the first record and break it.
            _writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };

            PurgeOldFiles();
            SdLog.Event("Journal", "Journal spool opened at {0}", path);
            return _writer;
        }
        catch (Exception ex)
        {
            _sinkFailed = true;
            SdLog.Fail("Journal", ex, "Journal spool unavailable — executions will not be recorded");
            return null;
        }
    }

    private void PurgeOldFiles()
    {
        try
        {
            var cutoff = DateTime.UtcNow.AddDays(-RetentionDays);
            foreach (var file in Directory.GetFiles(_directory, "executions-*.ndjson"))
            {
                if (File.GetLastWriteTimeUtc(file) < cutoff) File.Delete(file);
            }
        }
        catch (Exception ex)
        {
            // Housekeeping must never cost a fill: a file locked by the uploader is normal.
            SdLog.Warn("Journal purge skipped: {0}", ex.Message);
        }
    }

    private void CloseWriter()
    {
        try { _writer?.Dispose(); }
        catch (Exception ex) { SdLog.Warn("Journal spool not closed cleanly: {0}", ex.Message); }
        finally { _writer = null; }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _disposed = true;
            CloseWriter();
        }
    }
}
```

- [ ] **Étape 2 : brancher la source des exécutions**

Dans `OrderMonitor.cs`, ajouter le champ et le paramètre :

```csharp
    private readonly ExecutionRecorder? _recorder;

    public OrderMonitor(BridgeClient bridge, GuardEnforcer? enforcer = null,
                        ExecutionRecorder? recorder = null)
    {
        _bridge = bridge;
        _enforcer = enforcer;
        _recorder = recorder;
    }
```

dans `AttachOne()` (tâche 4) :

```csharp
            // NewMyTrades on EACH connector the registry aggregates, never ChartStrategy.OnNewMyTrade
            // and never a single connector. That difference is the reason the journal exists: at
            // connector level we see every fill on every account the platform is connected to,
            // including the ones placed at the DOM or the Chart Trader — the very ones the guard
            // rules cannot prevent, only report.
            connector.NewMyTrades += OnNewMyTrades;
```

dans `DetachOne()` : `connector.NewMyTrades -= OnNewMyTrades;`

et le gestionnaire :

```csharp
    private void OnNewMyTrades(IDataFeedConnector connector, IEnumerable<MyTrade> trades) =>
        Safely("Journal", () =>
        {
            if (_recorder == null) return;
            foreach (var trade in trades) _recorder.Record(trade);
            // A fill moved the position: refresh the deck rather than waiting for the next tick.
            StateChanged?.Invoke();
        });
```

- [ ] **Étape 3 : créer l'enregistreur dans la stratégie**

Dans `TradeDeckStrategy.cs`, ajouter `private ExecutionRecorder? _recorder;`, et dans `OnStarted()`,
**avant** la création du moniteur :

```csharp
            _recorder = new ExecutionRecorder(this);
            _monitor = new OrderMonitor(_bridge, _guard, _recorder);
```

Dans `OnStopping()`, **après** la libération du moniteur :

```csharp
        // After the monitor, which detaches the subscription that feeds it: closing the spool first
        // would leave fills arriving at a disposed writer.
        try { _recorder?.Dispose(); }
        catch (Exception ex) { SdLog.Fail("Session", ex, "Error while closing the journal spool"); }
```

et dans `OnStopped()` : `_recorder = null;`.

- [ ] **Étape 4 : compiler**

```bash
dotnet build src/Atas.Strategy.TradeDeck/Atas.Strategy.TradeDeck.csproj -c Release
```

Attendu : `Build succeeded`.

- [ ] **Étape 5 : vérifier — recette étape 8**

Sur compte Sim, avec le deck **appairé à Bitlearn** (sans quoi il n'y a pas de clé et les lignes
partent non scellées, ce qui est un cas valide mais pas celui qu'on vérifie ici) :

1. Passer un ordre au marché depuis le deck, puis **un second à la souris dans le DOM d'ATAS**.
2. Ouvrir le spool :
   ```powershell
   Get-Content "$env:APPDATA\StreamDeckTrader\journal\executions-$(Get-Date -f yyyy-MM-dd).ndjson" -Tail 5
   ```

Attendu, ligne par ligne :
- **une ligne par exécution, les deux présentes** — c'est ce qui prouve que la source est bien
  `NewMyTrades` sur les connecteurs agrégés et non le rappel de la stratégie ;
- chaque ligne porte `seq` et `sig` ;
- `orderName` vaut `"StreamDeck"` sur la première et `""` (ou la classification du gestionnaire) sur
  la seconde ;
- `commission` est un nombre ou `null`, jamais `0` inventé ;
- `tradingDay` correspond à la date locale.

3. **Contrôle de `pointValue`, le risque principal du portage.** Pour **chaque contrat réellement
   tradé** — pas seulement en théorie — comparer le `pointValue` de la ligne à la valeur connue :

   | Contrat | tickSize | tickCost | pointValue attendu |
   |---|---|---|---|
   | MNQ | 0.25 | 0.50 | **2** |
   | NQ | 0.25 | 5.00 | **20** |
   | MES | 0.25 | 1.25 | **5** |
   | ES | 0.25 | 12.50 | **50** |

   La ligne ES est **déjà validée en direct** (essai terrain du 24/08, ESU6 : `TickCost=12.5`,
   `TickSize=0.25` → `pointValue=50`, exact) : ce n'est plus une valeur à découvrir mais un attendu
   auquel comparer. Un écart ici fausse le P&L de tout le journal, **et personne ne s'en aperçoit
   avant les statistiques**. Si le tableau ne correspond pas, s'arrêter : c'est la dérivation qu'il
   faut corriger, jamais le tableau.

4. **Contrôle de l'horodatage.** Relever la ligne `Journal | First fill timestamp: raw=… kind=… → …`
   dans `addon-*.log` et comparer l'heure enregistrée à celle qu'ATAS affiche dans sa liste de
   trades. Si `kind=Utc`, l'offset est `+00:00` et c'est correct ; si `kind=Unspecified`, vérifier
   que l'heure locale enregistrée est bien celle affichée — sinon le décalage se retrouvera sur tout
   le journal.

5. **Contrôle du téléversement.** Laisser tourner l'hôte TradeDeck deux minutes et vérifier dans
   `plugin-*.log` la ligne d'envoi du lot, avec un nombre d'exécutions scellées non nul et **aucun
   refus** côté Bitlearn. C'est ce qui prouve que le deck-host lit ce spool **sans modification** —
   l'objet de toute la tâche.

- [ ] **Étape 6 : committer**

```bash
git add src/Atas.Strategy.TradeDeck/ binaries/
git commit -m "Journaliser et sceller les executions ATAS dans le spool que l hote televerse"
```

---

## Tâche 10 : déploiement et documentation

Fin de tâche : quelqu'un qui n'a pas suivi ce portage sait installer la stratégie, la diagnostiquer,
et sait ce qui reste à faire.

**Fichiers :**
- Créer : `docs/atas-chemin-ordre.md`
- Modifier : `docs/carte-des-deux-depots.md`
- Modifier : `binaries/README.md`
- Modifier : `CLAUDE.md`
- Modifier : `CHANGELOG.md`

- [ ] **Étape 1 : écrire la note de déploiement**

Créer `docs/atas-chemin-ordre.md` :

```markdown
# Le chemin d'ordre sur ATAS classique

> Rédigé en français comme le reste de la documentation ; le code et ses commentaires restent en
> anglais.

`src/Atas.Strategy.TradeDeck/` remplace l'add-on NinjaTrader pour un trader qui utilise **ATAS
classique** — les Chart Strategies n'existent pas sur ATAS X (Beta), confirmé par écrit par le
support ATAS. **Un trader utilise NinjaTrader OU ATAS** : le bridge n'accepte qu'un add-on à la fois.

Ni le bridge ni le deck-host ne savent qui est au bout du fil. Ils ne connaissent que
[`protocol.md`](protocol.md), et c'est la propriété qui a rendu ce portage possible : **aucune ligne
des deux n'a été modifiée.**

## Installer

```powershell
dotnet build src\Atas.Strategy.TradeDeck\Atas.Strategy.TradeDeck.csproj -c Release
Copy-Item src\Atas.Strategy.TradeDeck\bin\Release\net10.0\TradeDeck.Strategy.dll `
          "$env:APPDATA\ATAS\Strategies\" -Force
```

**Puis redémarrer ATAS.** Contrairement à ATAS X, le classique **ne surveille pas** ce dossier : le
DLL déposé pendant qu'ATAS tourne n'est vu qu'au prochain démarrage. La stratégie se charge alors sur
un graphique quelconque, par clic droit → ***Chart strategies*** (« Stratégies de graphique ») →
**TradeDeck** → *Démarrer*.

**Le graphique doit avoir un portefeuille sélectionné dans ses paramètres et un flux de données
connecté** : sans les deux, ATAS refuse purement et simplement de démarrer la stratégie.

**Le graphique n'est qu'un hôte, pas la source de la sélection.** `ChartStrategy.Connector` est
toujours nul sur le classique — mesuré, confirmé par le support ATAS — donc la sélection du deck ne
se résout pas contre lui : elle se résout contre le **registre de connecteurs de l'hôte**
(`OFT.Platform.Core`, atteint par réflexion), agrégé sur tous les connecteurs connectés, et posée
explicitement sur chaque ordre. Si ce registre devient introuvable, la stratégie **dégrade** vers le
compte et l'instrument du graphique seuls, le dit une fois dans le journal, et continue de trader.

**Le graphique arrête la stratégie silencieusement** dans plusieurs cas, à connaître avant de
diagnostiquer une publication d'état qui s'interrompt sans raison apparente : changement
d'instrument, d'unité de temps ou de portefeuille du graphique ; rechargement de l'historique ;
fermeture du graphique ; redépôt du DLL. Dans tous ces cas, elle cesse d'être un client du bridge, et
le bridge signale « add-on déconnecté » exactement comme quand NinjaTrader est fermé — le deck
affiche déjà cet état.

## Diagnostiquer

Même fichier qu'avec NinjaTrader : `%APPDATA%\StreamDeckTrader\logs\addon-AAAA-MM-JJ.log`, même
format de ligne, même horloge. Le flux de [`logging-strategy.md`](logging-strategy.md) fonctionne
sans rien changer. Le basculement en mode dégradé (registre de connecteurs introuvable) s'y trouve
lui aussi, journalisé une fois au démarrage.

`TD_LOG_LEVEL=TRACE` dans l'environnement d'ATAS ouvre les trames WebSocket et la publication d'état
— des dizaines de milliers de lignes par jour, à ne laisser que le temps d'un diagnostic.

## Ce qui n'est pas porté

| Fonction | État |
|---|---|
| Macro Tendance | **absente**. Le bloc `trend` n'est pas publié ; la touche affiche `NO DATA`, ce qui est exact |
| Copieur multi-comptes | **absent**. `setCopierConfig` et `copierPanic` sont refusés en `UNSUPPORTED_ACTION` |
| Élagage des comptes « roulés » de prop firm | non porté : heuristique propre à la façon dont NinjaTrader énumère ces comptes |

## Les pièges à connaître

- **`Portfolio.ClosedPnL`, jamais `TotalClosedPnL`.** Le second cumule les séances ; le premier porte
  une date. La perte journalière se mesure « depuis le PnL de début de journée » : prendre le
  cumulatif ferait franchir le plafond dès l'ouverture sur un compte en perte historique, et la
  macro refuserait tout, indéfiniment.
- **`pointValue` est dérivé — `TickCost / TickSize`.** C'est le seul champ calculé du journal, et le
  serveur calcule le P&L dessus sans table d'instruments à lui. Faux, il fausse tout le journal, et
  personne ne s'en aperçoit avant les statistiques. Validé sur ESU6 réel (`50 = 12.5 / 0.25`), mais à
  revérifier **contrat par contrat**, sur ceux réellement tradés.
- **`ChartStrategy.Connector` est toujours nul, et `ChartStrategy.Orders` vaut toujours 0.** Ne
  jamais s'y fier ni les retenter en repli : ce n'est pas un cas rare, c'est systématique sur le
  classique. L'accès réel passe par le registre de connecteurs de l'hôte, en réflexion pure — voir
  `docs/superpowers/specs/2026-08-21-portage-chemin-ordre-atas-design.md`.

## Le fichier partagé

`JournalSeal.cs` vit dans `src/NinjaTrader.AddOn.StreamDeck/Utilities/` et est compilé **par les
deux** : NinjaScript chez le trader, et le projet ATAS par lien MSBuild. Il reste donc en **C# 9 /
`net48`**, et sa forme canonique doit rester identique **au caractère près** à
`deck-host/src/seal.ts` et à `Bitlearn/lib/tradeDeck/integrity.js`. Après toute modification :
`dotnet build src/NinjaTrader.Scripts` **et** le build ATAS, les deux ou aucun.
```

- [ ] **Étape 2 : signaler le nouveau projet dans la carte des dépôts**

Dans `docs/carte-des-deux-depots.md`, ajouter à l'inventaire des composants du moteur, à côté de
l'add-on NinjaTrader :

```markdown
| `src/Atas.Strategy.TradeDeck/` | La même chose pour ATAS classique : une `ChartStrategy` qui parle le même protocole au bridge. Un trader utilise NinjaTrader **ou** ATAS. Voir [atas-chemin-ordre.md](atas-chemin-ordre.md) |
```

- [ ] **Étape 3 : ajouter le nouveau binaire archivé dans `binaries/README.md`**

`src/Directory.Build.targets` archive déjà l'assembly du projet dans `binaries/Atas.Strategy.TradeDeck/`
à chaque build Release, mais le tableau de `binaries/README.md` ne le liste pas encore — il ne cite
que `Atas.Indicators.TradeDeck/`, `StreamDeckBridge/`, `NinjaTrader.AddOn.StreamDeck/` et
`NinjaTrader.Scripts/`. Ajouter une ligne :

```markdown
| `Atas.Strategy.TradeDeck/` | net10.0 | **Livrable réel** : c'est ce `TradeDeck.Strategy.dll` qu'ATAS classique charge depuis `%APPDATA%\ATAS\Strategies\`. |
```

- [ ] **Étape 4 : mettre `CLAUDE.md` à jour**

Trois ajouts, aux endroits qui existent déjà :

1. Dans **Commandes**, après le bloc des indicateurs NinjaScript :

```bash
# Stratégie ATAS classique — le chemin d'ordre (voir docs/atas-chemin-ordre.md)
dotnet build "src/Atas.Strategy.TradeDeck/Atas.Strategy.TradeDeck.csproj" -c Release
```

2. Dans **Déploiement**, après le point sur l'add-on NT8 :

```markdown
- **la stratégie ATAS classique** se dépose en DLL dans `%APPDATA%\ATAS\Strategies\`, dossier que le
  classique ne surveille pas — **redémarrer ATAS** avant de la charger depuis le menu contextuel d'un
  graphique (*Chart strategies*). Un trader utilise NinjaTrader **ou** ATAS : le bridge n'accepte
  qu'un add-on à la fois.
```

3. Dans **Pièges à connaître**, modifier le dernier point pour y intégrer le troisième fichier
   partagé — il en cite déjà deux :

```markdown
- **`TdSwingEngine.cs`, `TdStopSelector.cs` et `JournalSeal.cs` sont compilés par PLUSIEURS chaînes**
  — NinjaScript chez le trader, `NinjaTrader.Scripts.csproj`, et les projets ATAS
  `src/Atas.Indicators.TradeDeck/` et `src/Atas.Strategy.TradeDeck/` qui les lient par
  `<Compile Include>`. Ils doivent donc rester en **C# 9 / `net48`** — namespace à portée de fichier,
  `record` ou `required` compilent côté ATAS et cassent la compilation NinjaScript **entière** chez
  le trader, indicateurs personnels compris. Après toute modification de l'un d'eux :
  `dotnet build src/NinjaTrader.Scripts` **et** les builds ATAS, tous ou aucun.
  `JournalSeal.cs` a une contrainte de plus : sa forme canonique doit rester identique **au
  caractère près** à `deck-host/src/seal.ts` et à `Bitlearn/lib/tradeDeck/integrity.js`, faute de
  quoi plus rien ne scelle — silencieusement, puisque ce côté-ci croit toujours signer correctement.
```

- [ ] **Étape 5 : compléter le journal des versions**

Dans `CHANGELOG.md`, sous la section « Non publié » déjà ouverte en tâche 1 :

```markdown
- Chemin d'ordre porté sur ATAS classique : `src/Atas.Strategy.TradeDeck/` exécute les 13 commandes,
  publie l'état toutes les 500 ms, remonte les rejets, applique la macro de sécurité aux ordres
  passés hors du deck et journalise les exécutions scellées. Le bridge et le deck-host sont
  inchangés. La macro Tendance et le copieur multi-comptes ne sont pas portés.
```

- [ ] **Étape 6 : vérifier que rien du bridge ni de l'hôte n'a bougé**

```bash
git diff --stat main -- src/StreamDeckBridge src/deck-host
```

Attendu : **aucune sortie**. La moindre ligne signale qu'on a cédé quelque part sur la contrainte
qui rend ce portage possible.

- [ ] **Étape 7 : compiler les trois projets une dernière fois**

```bash
dotnet build src/NinjaTrader.AddOn.StreamDeck/NinjaTrader.AddOn.StreamDeck.csproj -c Release
dotnet build src/NinjaTrader.Scripts/NinjaTrader.Scripts.csproj -c Release
dotnet build src/Atas.Strategy.TradeDeck/Atas.Strategy.TradeDeck.csproj -c Release
```

Attendu : `Build succeeded` sur les trois.

- [ ] **Étape 8 : committer**

```bash
git add docs/ binaries/README.md CLAUDE.md CHANGELOG.md
git commit -m "Documenter le chemin d ordre ATAS : installation, diagnostic et limites"
```

---

## Après ce plan

Deux choses restent, dans cet ordre :

1. **Reconstruire l'installateur** (`docs/publier-une-version.md`). La tâche 1 a changé le namespace
   de `JournalSeal.cs`, **qui est livré chez le trader** : sans un installateur reconstruit, un poste
   NinjaTrader existant se retrouve avec un `ExecutionRecorder` référençant un namespace absent, et
   une compilation NinjaScript en échec — tout ou rien, indicateurs du trader compris.
2. **La spécification suivante** : macro Tendance et copieur multi-comptes sur ATAS. Les deux touches
   correspondantes affichent aujourd'hui un état honnête (`NO DATA`, suiveurs non résolus), ce qui
   n'est pas une régression mais reste une fonction en moins pour un trader ATAS.
