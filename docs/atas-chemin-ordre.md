# Le chemin d'ordre sur ATAS classique

> Rédigé en français comme le reste de la documentation ; le code et ses commentaires restent en
> anglais.

`src/Atas.Strategy.TradeDeck/` remplace l'add-on NinjaTrader pour un trader qui utilise **ATAS
classique** — les Chart Strategies n'existent pas sur ATAS X (Beta), confirmé par écrit par le
support ATAS. **Un trader utilise NinjaTrader OU ATAS** : le bridge n'accepte qu'un add-on à la fois.

Ni le bridge ni le deck-host ne savent qui est au bout du fil. Ils ne connaissent que
[`protocol.md`](protocol.md), et c'est la propriété qui a rendu ce portage possible : **aucune ligne
des deux n'a été modifiée** — vérifié sur l'ensemble de la branche, pas seulement le dernier commit
(voir « Vérifié » plus bas).

**Validé sur la plateforme réelle le 25/08/2026** : avec la stratégie démarrée sur un graphique
quelconque, le trader pilote compte **et** instrument depuis le boîtier, y compris un compte porté
par un **autre connecteur** que celui du graphique. `ChartStrategy.OpenOrder` respecte le `Portfolio`
et le `Security` posés sur l'ordre. Trois comptes sur deux connecteurs (`SbeConnector`,
`RithmicConnector`) ont été enchaînés depuis le deck ; les ordres au marché sont partis en 42 à
103 ms ; six rejets du broker ont produit exactement six trames `orderUpdate`, une par rejet.

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
compte et l'instrument du graphique seuls (via le `TradingManager` protégé de l'indicateur), le dit
une fois dans le journal, et continue de trader. **En mode dégradé il n'y a plus d'événements de
connecteur** : les ordres passés hors du deck ne sont plus surveillés par `GuardEnforcer`, et le
journal d'exécution s'arrête. La macro de sécurité côté bridge reste alors le seul filet.

**Une règle d'exploitation à connaître, pas un bug** : `IDataFeedConnector.Securities` ne liste que
les instruments **actuellement souscrits** par la plateforme, pas le catalogue du broker. Mesuré :
`MES` ne résolvait à rien et trois `buyMarket` ont été correctement refusés `INSTRUMENT_NOT_FOUND`,
jusqu'à ce que le trader ouvre un graphique ou un DOM sur MES — après quoi le même nom se résolvait
et les ordres partaient. Donc : **un instrument n'est joignable depuis le deck que si un graphique ou
un DOM d'ATAS l'affiche déjà.** C'est le point qui surprendra le plus un nouvel utilisateur.

**Un `Security` ne doit jamais être associé à un `Portfolio` d'un autre connecteur.** Un essai en ce
sens a produit un vrai rejet du broker (« Are your ticker symbol and exchange values valid? »).
`FindSecurity` est donc scopé au connecteur qui porte le portefeuille résolu, avec un repli
plateforme entière journalisé si le connecteur scopé échoue.

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
| Macro Tendance | **absente**. `configureTrend` répond `UNSUPPORTED_ACTION` ; le bloc `trend` n'est pas publié, la touche affiche `NO DATA`, ce qui est exact |
| Copieur multi-comptes | **absent**. `setCopierConfig` et `copierPanic` répondent `UNSUPPORTED_ACTION` |
| Élagage des comptes « roulés » de prop firm | non porté : heuristique propre à la façon dont NinjaTrader énumère ces comptes |

## Les pièges à connaître

- **`Portfolio.ClosedPnL`, jamais `TotalClosedPnL`.** Le second cumule les séances ; le premier porte
  une date. La perte journalière se mesure « depuis le PnL de début de journée » : prendre le
  cumulatif ferait franchir le plafond dès l'ouverture sur un compte en perte historique, et la
  macro refuserait tout, indéfiniment.
- **`pointValue` est dérivé — `TickCost / TickSize`.** C'est le seul champ calculé du journal, et le
  serveur calcule le P&L dessus sans table d'instruments à lui. Faux, il fausse tout le journal, et
  personne ne s'en aperçoit avant les statistiques. Validé sur **un seul** contrat réel, ESU6
  (`50 = 12.5 / 0.25`), mais à revérifier **contrat par contrat**, sur ceux réellement tradés.
- **`ChartStrategy.Connector` est toujours nul, et `ChartStrategy.Orders` vaut toujours 0.** Ne
  jamais s'y fier ni les retenter en repli : ce n'est pas un cas rare, c'est systématique sur le
  classique. L'accès réel passe par le registre de connecteurs de l'hôte, en réflexion pure — voir
  `docs/superpowers/specs/2026-08-21-portage-chemin-ordre-atas-design.md`.
- **`attachBracket` avec une jambe refusée et l'autre posée répond `success: true`.** C'est le
  protocole qui l'impose, pas un choix de ce portage — mais c'est le cas non borné : une position
  avec un objectif, **sans stop**, et une touche verte qui ne le montre pas.

## Ce qui reste à vérifier sur machine

Ces points ne peuvent pas se trancher en lecture de code ; ils sont dus avant de considérer le
portage clos :

- le piège `ClosedPnL` ci-dessus : comparer le `realizedPnl` publié avec le P&L **du jour** d'ATAS,
  pas le cumulatif, sur une vraie séance ;
- si `Order.Clone()` recopie `TimeInForce` ;
- si `ModifyOrder` respecte `OCOGroup` lors d'une modification — sinon un stop orphelin survit à un
  take-profit rempli et déclenche une entrée inverse sans surveillance ;
- le scénario d'un scale-in suivi d'un fill partiel de take-profit.

## Le fichier partagé

`JournalSeal.cs` vit dans `src/NinjaTrader.AddOn.StreamDeck/Utilities/` et est compilé **par les
deux** : NinjaScript chez le trader, et le projet ATAS par lien MSBuild. Il reste donc en **C# 9 /
`net48`**, et sa forme canonique doit rester identique **au caractère près** à
`deck-host/src/seal.ts` et à `Bitlearn/lib/tradeDeck/integrity.js`. Après toute modification :
`dotnet build src/NinjaTrader.Scripts` **et** le build ATAS, les deux ou aucun.
