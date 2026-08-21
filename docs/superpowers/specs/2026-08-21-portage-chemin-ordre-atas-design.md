# Portage du chemin d'ordre vers ATAS X — conception

> Périmètre : **les 13 commandes, la publication d'état, les refus et les rejets, et le journal.**
> La macro Tendance et le copieur multi-comptes font l'objet d'une spécification ultérieure.
> Le portage des indicateurs est décrit à part (`2026-08-21-portage-indicateurs-atas-design.md`) et
> n'a aucun lien avec celui-ci.

## Ce qu'on livre

Une `ChartStrategy` ATAS qui remplace l'add-on NinjaTrader, parle le même protocole WebSocket au
bridge, et permet de trader depuis le deck sur ATAS X.

**Le bridge et le deck-host ne sont pas touchés.** Ni `MessageRouter` ni le deck-host ne savent
qu'il y a NinjaTrader au bout du fil : ils ne connaissent que `docs/protocol.md`. Un composant ATAS
qui parle ce protocole se branche à leur place sans qu'ils s'en aperçoivent. C'est la propriété qui
rend ce portage envisageable, et il ne faut pas la casser en chemin.

Un trader utilise NinjaTrader **ou** ATAS : le bridge n'accepte qu'un add-on à la fois.

## Ce qu'ATAS permet réellement

La documentation ATAS laisse croire qu'une stratégie est enfermée dans son graphique. Les assemblies
disent autre chose, et c'est ce qui rend le cockpit portable. Établi par réflexion sur
`ATAS.Indicators.dll`, `ATAS.DataFeedsCore.dll` et `ATAS.Strategies.dll` :

- `ChartStrategy.Connector` donne un `IDataFeedConnector`, qui expose **`Portfolios`**,
  **`Securities`**, `GetPortfolios(Security)` et `SearchSecuritiesAsync(...)`, plus les événements
  `NewPortfolios` / `PortfoliosChanged` / `NewSecurities` ;
- le même connecteur expose **`Orders`, `Positions`, `MyTrades` au pluriel**, avec `NewOrders`,
  `NewPositions`, `PositionsChanged`, `NewMyTrades`, `OrderChanged`, `OrdersRegisterFailed`,
  `OrdersCancelFailed`, `OrderModifyFailed` — c'est-à-dire **tout ce qui se passe sur le compte**,
  quelle qu'en soit l'origine, et pas seulement ce que la stratégie a provoqué ;
- `Order` porte ses propres champs `Portfolio` et `Security` : un ordre n'est donc pas contraint à
  la sélection du graphique.

Conclusion : le sélecteur d'instrument, le sélecteur de compte, la surveillance des ordres externes
et le journal sont tous réalisables. La seule contrainte réelle est que **la stratégie doit être
attachée à un graphique et démarrée** — n'importe lequel, affichant n'importe quoi.

## Le composant

```
src/Atas.Strategy.TradeDeck/
  TradeDeckStrategy.cs      : ChartStrategy      point d'entrée et cycle de vie
  BridgeClient.cs                                ws://127.0.0.1:8219, reconnexion autonome
  CommandDispatcher.cs                           les 13 commandes
  OrderEngine.cs                                 construit et poste les Order
  ContextResolver.cs                             sélection du deck → Portfolio + Security
  OrderMonitor.cs                                rejets et transitions d'état
  StatePublisher.cs                              publication toutes les 500 ms
  GuardEnforcer.cs                               annulation des ordres extérieurs
  ExecutionRecorder.cs                           le spool du journal
```

Cycle de vie fourni par `ChartStrategy` : `OnStarted()` ouvre la WebSocket et s'abonne aux
événements du connecteur, `OnStopping()` / `OnStopped()` ferment proprement,
`StopWithNotification(string)` permet d'échouer **bruyamment** — un cockpit muet est pire qu'un
cockpit absent.

**Le graphique fermé ne casse rien de nouveau.** Stratégie arrêtée ou retirée, elle cesse d'être un
client du bridge, et le bridge signale « add-on déconnecté » exactement comme aujourd'hui quand
NinjaTrader est fermé. Le deck affiche déjà cet état.

### Ce qui remplace quoi

| Add-on NinjaTrader | ATAS | Sort |
|---|---|---|
| `StreamDeckAddOn` (341 l.) | `TradeDeckStrategy : ChartStrategy` | réécrit, plus court |
| `BridgeClient` (218 l.) | `ClientWebSocket` .NET 10 | réécrit — le verrou d'envoi **reste** |
| `CommandDispatcher` | idem | réécrit |
| `ContextResolver` (694 l.) | `Connector.Portfolios` / `.Securities` | réécrit, beaucoup plus court |
| `TradingEngine` (1059 l.) | `OrderEngine`, `Order` explicites | réécrit |
| `OrderMonitor` (230 l.) | `Connector.OrderChanged`, `OrdersRegisterFailed` | réécrit, plus court |
| `StatePublisher` (530 l.) | idem | réécrit |
| `GuardEnforcer` (365 l.) | `Connector.NewOrders` + `CancelOrder` | réécrit |
| `ExecutionRecorder` (401 l.) | idem, sur `Connector.NewMyTrades` | réécrit |
| `JournalSeal` (270 l.) | **PARTAGÉ** par lien MSBuild | inchangé |
| `SimpleJson` (454 l.) | supprimé — `Newtonsoft.Json` | — |
| `SdLogger` (425 l.) | `ILoggerSource` + même fichier `addon-AAAA-MM-JJ.log` | réécrit |
| `TrendEngine` / `TrendMonitor` | — | spec suivante |
| `CopyEngine` (1997 l.) | — | spec suivante |

**Le verrou d'envoi est reconduit.** `ClientWebSocket` n'autorise toujours qu'un `SendAsync` en vol
en .NET 10 : il lève au lieu d'aborter la socket comme en .NET 4.8, mais la sérialisation reste
obligatoire. Le garde-fou existe pour une raison qui n'a pas changé — perdre une confirmation
d'ordre.

## La sélection : le deck décide

La sélection du deck (`instrument`, `account`) est résolue contre `Connector.Securities` et
`Connector.Portfolios`, puis **posée explicitement** sur chaque ordre. Le graphique n'est qu'un
hôte : il peut afficher tout autre chose.

```csharp
var order = new Order {
    Portfolio = resolved.Portfolio,   // celui du DECK, pas celui du graphique
    Security  = resolved.Security,
    Direction = OrderDirections.Buy,
    Type      = OrderTypes.Market,
    QuantityToFill = qty,
};
TradingManager.OpenOrder(order, setDefaultQuantity: false, askConfirmation: false, checkOrderStates: true);
```

`askConfirmation: false` n'est pas un détail : une boîte de dialogue au milieu d'un appui de touche
transformerait le cockpit en piège.

**`SetStopLoss()`, `SetTakeProfit()` et `SetBreakeven()` sont écartés**, malgré leur existence dans
`ITradingManager` et les 1059 lignes de `TradingEngine` qu'ils sembleraient économiser. Ils agissent
sur la sélection du **graphique** : dès que le deck vise un instrument que le graphique n'affiche
pas, ils envoient le break-even sur la mauvaise position. Les protections passent donc par des
ordres explicites — ce qu'exige de toute façon la règle « Move Stop / Move Target déplacent **tous**
les ordres de protection, pas un au hasard ».

## Les 13 commandes

| Commande | Réalisation ATAS |
|---|---|
| `buyMarket` / `sellMarket` | `Order` de type `OrderTypes.Market` |
| `buyLimit` / `sellLimit` | `OrderTypes.Limit`, prix = dernier ± décalage, validité jour |
| `flatten` | ordre marché inverse sur la position résolue |
| `flattenAccount` | idem sur toutes les `Positions` du portefeuille |
| `cancelWorkingOrders` | `CancelOrder` sur les `Orders` actifs de la paire résolue |
| `cancelOrders` | `cancelWorkingOrders` puis `flatten` |
| `reverse` | annuler les protections, aplatir, ouvrir en sens inverse |
| `breakeven` | déplacer **tous** les stops au prix moyen ± décalage |
| `attachBracket` | poster stop et objectif liés à la position |
| `moveStop` / `moveTarget` | `ModifyOrder` sur **chaque** ordre de protection |

Les garde-fous existants sont reconduits, avec leurs codes d'erreur. `NO_MARKET_DATA` en
particulier : un ordre limite envoyé sans flux partirait à quelques ticks de 0, et une limite
vendeuse sous le marché s'exécute instantanément au marché. La source devient `BestBid` / `BestAsk`.

`GuardEnforcer` est **dans** le périmètre, et pas un supplément. Il annule les ordres passés hors du
deck — DOM, Chart Trader — qui ouvriraient ou agrandiraient une position pendant que la macro
refuse les entrées. Sans lui la macro se contourne en tapant l'ordre à la main, et « chaque limite
devient décorative au moment exact où elle compte ». Il ne réduit jamais une position et ne poste
jamais d'ordre : le pire cas d'un faux positif est un ordre annulé, jamais un trader enfermé.

## La publication d'état

| Champ du protocole | Source ATAS |
|---|---|
| `connected` | `Connector.ConnectionStateChanged`, `Portfolio.ConnectionState` |
| `account.name` | `Portfolio.AccountID` |
| `account.realizedPnl` | `Portfolio.ClosedPnL` — **et surtout pas `TotalClosedPnL`** |
| `account.unrealizedPnl` | `Portfolio.OpenPnL` |
| `instrument.tickSize` | `Security.TickSize` |
| `instrument.pointValue` | `Security.TickCost / Security.TickSize` |
| `instrument.lastPrice` | `BestBid` / `BestAsk` |
| `position.exists` / `direction` / `quantity` | `Position.IsInPosition`, signe et valeur de `Position.Volume` |
| `position.averagePrice` | `Position.AveragePrice` |
| `position.unrealizedPnl` | `Position.UnrealizedPnL` |
| `position.hasStopOrder` … `targetOrderCount` | `Connector.Orders` filtrés sur la paire résolue |
| `trend` | **absent** — hors périmètre |
| `quantity` | rien à faire : le bridge la possède et la fusionne |

Deux pièges, nommés maintenant plutôt que découverts en production :

**`ClosedPnL`, jamais `TotalClosedPnL`.** Le second cumule les sessions ; le premier porte une date
(`ClosedPnlDate`). La perte journalière maximale se mesure « depuis le PnL de début de journée » :
prendre le cumulatif ferait franchir le plafond dès l'ouverture à un compte en perte historique, et
la macro refuserait tout, indéfiniment.

**`Portfolio.IsRealAccount` est le filtre Sim/réel.** Le cockpit expose `allowLiveAccounts`, « le
seul filtre entre Sim et réel ». ATAS le donne comme un fait plutôt que par convention de nommage :
c'est un gain de robustesse, pas une simple traduction.

Le bloc `trend` absent est **sûr par conception** : le protocole prévoit qu'il soit absent tant que
le monitor n'est pas initialisé, et que le bridge conserve alors la dernière valeur reçue. La touche
Tendance affichera `NO DATA`, ce qui est exact.

## Le journal

Il ne traverse pas le bridge. `ExecutionRecorder` écrit une ligne JSON par exécution dans
`%APPDATA%\StreamDeckTrader\journal\` ; le deck-host lit, scelle la chaîne et téléverse. **Le chemin
et le format sont une interface, pas un choix** — le deck-host reste intact.

Source des exécutions : **`Connector.NewMyTrades`**, et non `ChartStrategy.OnNewMyTrade`. La
différence est la raison d'être du journal : au niveau du connecteur, on voit toutes les exécutions
du compte, y compris celles passées au DOM ou au Chart Trader. C'est ce que le fichier revendique —
« le seul composant qui voit la vérité ».

| Champ scellé | Source | Note |
|---|---|---|
| `execId` | `MyTrade.Id` | |
| `orderId` | `MyTrade.OrderId` | |
| `account` | `MyTrade.AccountID` | |
| `instrument` | `MyTrade.Security.Code` | |
| `marketPosition` | `MyTrade.OrderDirection` | |
| `price` / `quantity` | `MyTrade.Price` / `.Volume` | |
| `commission` | `MyTrade.Commission` | nullable, comme aujourd'hui |
| `tickSize` | `Security.TickSize` | |
| `pointValue` | `Security.TickCost / TickSize` | **dérivé** |
| `orderName` | `MyTrade.Order`, plus `IsStopLossOrder` / `IsTakeProfitOrder` | à ajuster à l'implémentation |
| `trend` | omis | le serveur lit « inconnu » |
| `time` / `recordedAtUtc` / `tradingDay` | `MyTrade.Time` + horloge locale | |

**`pointValue` est le seul champ calculé, et le plus sensible** : le serveur calcule le P&L dessus,
sans table d'instruments à lui. NinjaTrader le fournit tout fait (`MasterInstrument.PointValue`),
ATAS non. La dérivation doit donner exactement le même nombre, à vérifier instrument par instrument
sur les contrats réellement tradés — pas seulement en théorie.

Les trois règles qui gouvernent cette classe sont reconduites, et la première prime sur tout :
**elle ne doit jamais pouvoir casser le trading.** Aucune exception ne s'en échappe, aucun appel
réseau, aucune attente bloquante — d'autant qu'ici on tourne dans le fil d'événements du connecteur.

### Le scellement rejoint les fichiers partagés

`JournalSeal.cs` est lié par MSBuild depuis le projet ATAS, sous les mêmes règles que
`TdSwingEngine` : namespace neutre, **C# 9 / `net48` obligatoire**, les deux builds passent ou aucun.

⚠ **Une différence avec les fichiers partagés des indicateurs : celui-ci est livré chez le trader.**
`TdStopSelector` ne quittait pas le poste de développement ; `JournalSeal.cs` fait partie de la
charge utile que l'installateur dépose dans `AddOns\StreamDeck\`. Le passer en namespace neutre
touche donc du code déployé, et impose une version et un installateur reconstruit
(`docs/publier-une-version.md`) — sans quoi le trader se retrouve avec un `ExecutionRecorder` qui
référence un namespace absent, et une compilation NinjaScript en échec, tout ou rien.

Si l'autre équipe préfère l'éviter, l'alternative est de laisser le namespace tel quel et
d'accepter un `NinjaTrader.*` dans l'assembly ATAS : sans conséquence fonctionnelle, contrairement
au risque ci-dessus. Le partage du fichier, lui, n'est pas négociable — c'est le point entier.

La raison est écrite dans le fichier : sa forme canonique doit rester identique **au caractère près**
à `deck-host/src/seal.ts` et à `Bitlearn/lib/tradeDeck/integrity.js`, faute de quoi « rien ne scelle
plus, et tous les journaux tombent silencieusement au niveau non vérifiable — silencieusement, parce
que ce côté-ci croit toujours signer correctement ». Une quatrième implémentation manuscrite serait
le prochain incident, déjà décrit par sa propre victime.

## Construire et déployer

```xml
<TargetFramework>net10.0</TargetFramework>
<ATAS_BASE Condition="'$(ATAS_BASE)'==''">C:\Program Files\ATAS X</ATAS_BASE>
```

Références : `ATAS.Indicators`, `ATAS.DataFeedsCore`, **`ATAS.Strategies`**, `OFT.Attributes`,
`OFT.Localization`, `Utils.Common`. Un seul paquet NuGet, `Newtonsoft.Json`, déjà présent chez ATAS.

Déploiement : le DLL dans `%APPDATA%\ATAS X\Strategies\`, puis chargement depuis le menu contextuel
du graphique.

Logs dans `%APPDATA%\StreamDeckTrader\logs\addon-AAAA-MM-JJ.log`, même nom qu'aujourd'hui : le flux
de diagnostic de `docs/logging-strategy.md` continue de fonctionner sans rien changer ailleurs. Les
règles de niveau sont reconduites — **`TRACE` obligatoire** dans la publication 500 ms, sous peine
de centaines de milliers de lignes par jour.

## Comment on vérifie

Le portage des indicateurs se vérifiait en comparant des nombres. Ici la seule preuve est un ordre
réellement exécuté.

**Toujours sur ports isolés**, jamais 8218/8219 pendant qu'une installation tourne. Et ne jamais
sonder 8218 : la connexion reste ouverte et la commande se bloque.

```powershell
$env:SDBRIDGE_PluginPort = "9318"; $env:SDBRIDGE_AddonPort = "9319"
```

Recette, dans cet ordre, chaque étape bloquant la suivante :

1. la stratégie se charge, démarre, et le bridge la voit comme add-on connecté ;
2. `stateUpdate` arrive toutes les 500 ms, compte et instrument corrects, position vide ;
3. **un** `buyMarket` sur compte Sim produit un ordre, un fill, et une position visible sur la
   touche ;
4. le rejet remonte : ordre volontairement invalide → `OrdersRegisterFailed` → code d'erreur sur la
   touche. C'est le point que `Account.Submit` asynchrone avait rendu douloureux chez NinjaTrader —
   un « OK » ne signifiait pas accepté ;
5. `NO_MARKET_DATA` refuse bien une limite hors flux ;
6. les 12 autres commandes, une par une, position ouverte ;
7. `GuardEnforcer` : macro armée, ordre tapé **dans le DOM ATAS** → annulé, deck informé ;
8. le journal : une ligne par exécution, scellée, et le deck-host la téléverse sans modification.

Les étapes 3 et 4 sont le vrai jalon : elles prouvent le transport, la sérialisation, la résolution
compte/instrument et la remontée des rejets. Tout le reste est variation sur une plomberie prouvée,
et c'est pourquoi elles se font avant les douze autres commandes et non après.

## Les vrais risques

- **`pointValue` dérivé.** Faux, il fausse le P&L de tout le journal, et personne ne s'en aperçoit
  avant les statistiques.
- **Le fil d'exécution.** Les événements du connecteur n'arrivent pas sur le fil du graphique. Toute
  écriture d'état partagé doit être protégée, et rien ne doit bloquer dans un gestionnaire
  d'événement — la contrainte qui a produit `_sendLock` chez NinjaTrader se pose ici sous une autre
  forme.
- **`checkOrderStates` et `askConfirmation`.** Deux booléens d'`OpenOrder` dont le comportement
  exact n'est pas documenté. À établir empiriquement avant de leur faire confiance : une
  confirmation qui s'ouvre pendant un appui de touche, c'est un cockpit qui ment.
- **`orderName`.** NinjaTrader nomme ses ordres de protection ; ATAS ne semble pas offrir
  d'équivalent direct, et le champ sert au serveur à distinguer une sortie au stop d'une sortie à
  l'objectif. À reconstruire depuis `IsStopLossOrder` / `IsTakeProfitOrder`, et à valider contre ce
  que le serveur attend.
