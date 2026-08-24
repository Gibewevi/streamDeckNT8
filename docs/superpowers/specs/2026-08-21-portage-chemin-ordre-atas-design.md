# Portage du chemin d'ordre vers ATAS — conception

> Périmètre : **les 13 commandes, la publication d'état, les refus et les rejets, et le journal.**
> La macro Tendance et le copieur multi-comptes font l'objet d'une spécification ultérieure.
> Le portage des indicateurs est décrit à part (`2026-08-21-portage-indicateurs-atas-design.md`) et
> n'a aucun lien avec celui-ci.

## Ce qu'on livre

Une `ChartStrategy` ATAS qui remplace l'add-on NinjaTrader, parle le même protocole WebSocket au
bridge, et permet de trader depuis le deck sur **ATAS classique**.

> **La cible est l'ATAS classique, pas ATAS X.** Le support ATAS l'a confirmé par écrit :
> « Chart Strategies are not currently supported in ATAS X (Beta) ». Le module n'existe pas
> là-bas ; il est prévu, sans date. Tout ce document vise donc `C:\Program Files (x86)\ATAS
> Platform`.

**Le bridge et le deck-host ne sont pas touchés.** Ni `MessageRouter` ni le deck-host ne savent
qu'il y a NinjaTrader au bout du fil : ils ne connaissent que `docs/protocol.md`. Un composant ATAS
qui parle ce protocole se branche à leur place sans qu'ils s'en aperçoivent. C'est la propriété qui
rend ce portage envisageable, et il ne faut pas la casser en chemin.

Un trader utilise NinjaTrader **ou** ATAS : le bridge n'accepte qu'un add-on à la fois.

## Ce qu'ATAS permet réellement

La documentation ATAS laisse croire qu'une stratégie est enfermée dans son graphique, et le support
le confirme par écrit : « the chart strategy is **intentionally scoped to the chart context**, not a
global account enumerator ». C'est vrai de l'API de `ChartStrategy`. Ça ne l'est pas du processus,
et c'est cette nuance qui rend le cockpit portable.

Établi **par mesure sur la plateforme installée**, pas par lecture de la documentation.

### Ce qui ne marche pas, et qu'il ne faut pas réessayer

**`ChartStrategy.Connector` est toujours `null`.** Mesuré sur 472 trames publiées, sur plusieurs
sessions. Le support l'a confirmé : la propriété existe, mais l'hôte du graphique ne la renseigne
jamais — « the connector must be assigned manually if you need it ». Le graphique fournit `Portfolio`
et `Security`, rien d'autre.

**`ChartStrategy.Orders` vaut toujours 0**, même quand un ordre existe. Ne jamais s'en servir.

**`TradingManager` fonctionne mais ne voit que le graphique.** `TradingManager.Portfolio` et
`.Security` sont les *mêmes références* que celles du graphique, et `TradingManager.Orders` ne montre
que le couple compte + instrument affiché. Un ordre passé sur un autre compte y est invisible —
vérifié en en plaçant un et en le laissant vivre.

### Ce qui marche : le registre de l'hôte

L'hôte, `OFT.Platform.Core.dll`, est chargé dans le **même processus** que la stratégie et expose un
registre de connecteurs en types et méthodes **publics**. Il s'atteint par
`CommonServiceLocator.ServiceLocator`, une API **statique publique** livrée avec la plateforme :

```csharp
ServiceLocator.IsLocationProviderSet                        // vrai : Autofac est en place
ServiceLocator.Current.GetInstance(typeof(ITradingCore))    // OFT.Platform.Core.Providers
    .GetConnector(portfolio)  →  IDataFeedConnector         // le connecteur qui manquait
ServiceLocator.Current.GetInstance(typeof(IConnectorsManager))
    .Connectors               →  tous les connecteurs de la plateforme
```

Mesuré : `GetConnector(DEMO29ADC)` rend un `SbeConnector` exposant `Portfolios`, `Securities`,
`Orders`, `Positions` et `MyTrades` — deux ordres et deux positions là où `TradingManager` n'en
voyait aucun au même instant. Et `IConnectorsManager.Connectors` liste 12 connecteurs, dont un
Rithmic portant deux portefeuilles absents du graphique.

Le sélecteur d'instrument, le sélecteur de compte, la surveillance des ordres extérieurs et le
journal sont donc tous réalisables, comme prévu. `Order` porte ses propres champs `Portfolio` et
`Security` : un ordre n'est pas contraint à la sélection du graphique.

⚠ **Deux écarts avec ce que cette spécification supposait d'abord.**

1. **Les portefeuilles sont répartis sur plusieurs connecteurs.** Il n'existe pas *un* connecteur
   qu'on interroge : `ContextResolver` doit **agréger** sur `IConnectorsManager.Connectors`, en
   ignorant ceux qui ne portent aucun portefeuille — 10 des 12 étaient dans ce cas.
2. **`CoreProvider.TradingCore` est nul.** Passer par `GetInstance(ITradingCore)` directement.

### Le prix, et comment on le paie

`OFT.Platform.Core` **n'est pas l'API plugin documentée** — c'est l'hôte. Les types et méthodes
employés sont publics, il n'y a aucune réflexion sur du privé, mais ATAS ne garantit rien et ne
préviendra pas s'ils bougent. Le support a d'ailleurs répondu trois fois « ce n'est pas possible »,
en parlant à chaque fois de l'API de `ChartStrategy`.

D'où une règle qui n'est pas négociable : **cet accès se fait entièrement par réflexion, sans une
seule référence de compilation** à `OFT.Platform.Core`, `OFT.Platform` ou `CommonServiceLocator`.
Ce n'est pas une élégance. Une référence dure et la stratégie ne se charge plus du tout le jour où
un de ces types est renommé : cockpit mort, sans explication. Par réflexion, elle **dégrade**.

**Le mode dégradé fait partie de la livraison.** Quand le registre est introuvable, la stratégie
retombe sur `TradingManager` — un seul compte, un seul instrument, ceux du graphique — le dit une
fois dans le journal, et continue de trader. Il doit être écrit et testé comme le reste : c'est le
filet qui transforme une rupture d'API en fonctionnalité réduite plutôt qu'en panne. Il n'est pas du
code mort.

La seule contrainte qui demeure est que **la stratégie doit être attachée à un graphique et
démarrée** — n'importe lequel, affichant n'importe quoi, mais avec un portefeuille sélectionné dans
ses paramètres et un flux de données connecté, faute de quoi ATAS refuse de la démarrer.

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
| `ContextResolver` (694 l.) | `IConnectorsManager.Connectors` agrégés → `Portfolios` / `Securities` | réécrit |
| `TradingEngine` (1059 l.) | `OrderEngine`, `Order` explicites | réécrit |
| `OrderMonitor` (230 l.) | `IDataFeedConnector.OrderChanged`, `OrdersRegisterFailed` | réécrit, plus court |
| `StatePublisher` (530 l.) | idem | réécrit |
| `GuardEnforcer` (365 l.) | `IDataFeedConnector.NewOrders` + `CancelOrder` | réécrit |
| `ExecutionRecorder` (401 l.) | idem, sur `IDataFeedConnector.NewMyTrades` | réécrit |
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

La sélection du deck (`instrument`, `account`) est résolue en **agrégeant les portefeuilles et
les instruments de tous les connecteurs connectés** (`IConnectorsManager.Connectors`), puis
**posée explicitement** sur chaque ordre. Le graphique n'est qu'un hôte : il peut afficher tout
autre chose.

En mode dégradé — registre introuvable — la sélection se réduit au couple du graphique, et le
deck n'affiche qu'un compte et qu'un instrument.

```csharp
var order = new Order {
    Portfolio = resolved.Portfolio,   // celui du DECK, pas celui du graphique
    Security  = resolved.Security,
    Direction = OrderDirections.Buy,
    Type      = OrderTypes.Market,
    QuantityToFill = qty,
};
OpenOrder(order);   // ChartStrategy.OpenOrder(Order) — l'API documentee d'une strategie
```

**C'est `ChartStrategy.OpenOrder(Order)` qu'on emploie, pas la surcharge à quatre booléens de
`ITradingManager`.** Cette dernière existe, mais la documentation et l'exemple officiel
`SmaChartStrategy` n'emploient que `OpenOrder(order)`, `CancelOrder(order)` et
`ModifyOrder(ancien, neuf)`, sans aucun booléen. Les trois sont présentes sur l'ATAS classique,
vérifié par réflexion. Cela retire du même coup la question `askConfirmation` : il n'y a pas de
paramètre, donc pas de boîte de dialogue à craindre au milieu d'un appui de touche.

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

**Vérifié compatible avec la règle « pas d'entrée avant une heure » (0.29.0), sans code
supplémentaire.** `GuardEnforcer` ne connaît aucune raison de refus en dur : il reçoit
`{blocked, reason, maxContracts}` de `setGuardPolicy` et applique le même test générique — l'ordre
agrandit-il l'exposition ? — quelle que soit `reason`. `beforeSessionStart` emprunte donc
exactement le même chemin que `dailyLoss` ou `tradeLimit`, et le composant ATAS n'a rien de
spécifique à écrire pour elle. L'heure locale, l'armement et le calcul du compte à rebours sont une
décision **du bridge seul** (`SafetyMacro`, non touché par ce portage) : le composant ATAS ne lit
jamais l'horloge, il relaie `setGuardPolicy` tel quel, comme pour toute autre règle de séance.

## La publication d'état

| Champ du protocole | Source ATAS |
|---|---|
| `connected` | `IDataFeedConnector.ConnectionStateChanged`, `Portfolio.ConnectionState` |
| `account.name` | `Portfolio.AccountID` |
| `account.realizedPnl` | `Portfolio.ClosedPnL` — **et surtout pas `TotalClosedPnL`** |
| `account.unrealizedPnl` | `Portfolio.OpenPnL` |
| `instrument.tickSize` | `Security.TickSize` |
| `instrument.pointValue` | `Security.TickCost / Security.TickSize` |
| `instrument.lastPrice` | `BestBid` / `BestAsk` |
| `position.exists` / `direction` / `quantity` | `Position.IsInPosition`, signe et valeur de `Position.Volume` |
| `position.averagePrice` | `Position.AveragePrice` |
| `position.unrealizedPnl` | `Position.UnrealizedPnL` |
| `position.hasStopOrder` … `targetOrderCount` | `IDataFeedConnector.Orders` filtrés sur la paire résolue |
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

Source des exécutions : **`IDataFeedConnector.NewMyTrades`**, et non `ChartStrategy.OnNewMyTrade`. La
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
<ATAS_BASE Condition="'$(ATAS_BASE)'==''">C:\Program Files (x86)\ATAS Platform</ATAS_BASE>
```

Références : `ATAS.Indicators`, `ATAS.DataFeedsCore`, **`ATAS.Strategies`**, `OFT.Attributes`,
`OFT.Localization`, `Utils.Common`. Un seul paquet NuGet, `Newtonsoft.Json`, déjà présent chez ATAS.

Déploiement : le DLL dans **`%APPDATA%\ATAS\Strategies\`**, puis **redémarrage d'ATAS** — le
classique ne surveille pas ce dossier, contrairement à ATAS X. La stratégie se charge ensuite par
clic droit sur le graphique → *Chart strategies*.

Le graphique doit avoir un portefeuille sélectionné dans ses paramètres et un flux connecté,
sinon ATAS refuse de démarrer la stratégie. Et il l'arrête **silencieusement** dès qu'on change
son instrument, son unité de temps ou son portefeuille, qu'on recharge l'historique, qu'on ferme
le graphique — ou qu'on redépose le DLL. C'est normal, et c'est à savoir avant de diagnostiquer
une publication d'état qui s'arrête.

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

0. le registre répond : le journal montre `IsLocationProviderSet=True`, `ITradingCore` résolu,
   `GetConnector(portefeuille)` non nul, et la liste des comptes de **tous** les connecteurs
   connectés. Si cette étape échoue, la stratégie doit basculer en mode dégradé **et le dire** —
   c'est ce basculement qu'on vérifie alors, pas la suite ;
1. la stratégie se charge, démarre, et le bridge la voit comme add-on connecté ;
2. `stateUpdate` arrive toutes les 500 ms, compte et instrument corrects, position vide ;
3. **un** `buyMarket` sur compte Sim produit un ordre, un fill, et une position visible sur la
   touche ;
4. le rejet remonte : ordre volontairement invalide → `OrdersRegisterFailed` → code d'erreur sur la
   touche. C'est le point que `Account.Submit` asynchrone avait rendu douloureux chez NinjaTrader —
   un « OK » ne signifiait pas accepté ;
5. `NO_MARKET_DATA` refuse bien une limite hors flux ;
6. les 12 autres commandes, une par une, position ouverte ;
7. `GuardEnforcer` : macro armée, ordre tapé **dans le DOM ATAS** → annulé, deck informé. Le cas le
   plus simple à rejouer à la demande est `beforeSessionStart` : régler l'heure quelques minutes
   dans le futur arme le refus sans attendre une vraie perte de journée, contrairement à
   `dailyLoss` ou `tradeLimit` qu'il faut provoquer par des trades réels ;
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
- **La dépendance à `OFT.Platform.Core`.** Le registre de connecteurs n'est pas l'API plugin
  documentée. Il peut disparaître à toute mise à jour d'ATAS, sans préavis. Atténué par l'accès
  en réflexion pure et par le mode dégradé — mais c'est le risque structurel de ce portage, et
  celui qu'il faut re-vérifier à chaque montée de version.
- ~~`checkOrderStates` et `askConfirmation`~~ — **écarté** : on passe par
  `ChartStrategy.OpenOrder(Order)`, qui ne prend aucun booléen.
- **`orderName`.** NinjaTrader nomme ses ordres de protection ; ATAS ne semble pas offrir
  d'équivalent direct, et le champ sert au serveur à distinguer une sortie au stop d'une sortie à
  l'objectif. À reconstruire depuis `IsStopLossOrder` / `IsTakeProfitOrder`, et à valider contre ce
  que le serveur attend.
