# CLAUDE.md — Add-On NinjaTrader 8

Contexte spécifique à ce dossier. Voir le `CLAUDE.md` racine pour l'architecture d'ensemble.

## Contrainte fondamentale

Ce projet est déployé **en sources `.cs`**, pas en DLL : NinjaScript les recompile dans
`NinjaTrader.Custom.dll` à chaque démarrage de NinjaTrader. Conséquences :

- **aucune dépendance externe à l'exécution.** Pas de NuGet, pas de Newtonsoft — d'où
  `Utilities/SimpleJson.cs` écrit à la main. Le `PackageReference` Newtonsoft du `.csproj` n'est
  pas utilisé par le code ;
- **C# 9 / .NET Framework 4.8** (`LangVersion` dans le `.csproj`), et `<Nullable>annotations</Nullable>` :
  les annotations `?` sont permises mais aucune analyse de nullité n'est faite. Rien au-delà de
  C# 9. Le code existant reste volontairement conservateur (pas de `switch` expressions, pas de
  pattern matching récent) pour rester lisible côté NinjaScript ;
- le `dotnet build` local ne sert **qu'à vérifier la compilation**. Il émet ~180 avertissements
  CS0436 (les types existent déjà dans le `NinjaTrader.Custom.dll` référencé) : c'est normal ;
- un fichier ajouté ici doit être copié dans le dossier de déploiement **à plat** — la structure
  `Models/ Services/ Utilities/` n'existe pas côté NinjaTrader.

## Règles de survie dans NinjaTrader

- **Ne jamais laisser une exception s'échapper vers NinjaTrader.** Les handlers d'événements
  (`OrderMonitor.OnOrderUpdate`, timers de `StatePublisher`) tournent dans le pipeline NT8 : une
  exception non interceptée y déstabilise la plateforme. Tout handler est enveloppé d'un
  `try/catch` qui journalise.
- **`ClientWebSocket` n'accepte qu'un `SendAsync` à la fois.** Le timer de publication d'état et
  la boucle de réception envoient tous les deux : d'où `_sendLock` dans `Services/BridgeClient.cs`.
  Un envoi concurrent abort la socket et fait perdre des réponses d'ordre.
- **`Account.Submit` est asynchrone.** Il ne lève pas d'exception sur un ordre refusé (marge,
  marché fermé, prix invalide) : le rejet arrive plus tard dans `OrderMonitor`, qui le renvoie au
  plugin en événement `orderUpdate`. Ne jamais conclure au succès depuis le retour de `Submit`.
- **`SdLogger` n'échoue jamais** et n'appelle la fenêtre de sortie NT8 qu'**après** avoir écrit
  dans le fichier, l'appel étant isolé : une défaillance d'initialisation des assemblies
  NinjaTrader se propageait sinon jusque dans le chemin d'envoi d'ordre.

## Points de vigilance métier

- `ContextResolver` résout un instrument par racine (« MNQ » → contrat courant) avec plusieurs
  stratégies successives (contrat daté, `GetInstrumentByDate`, prochaine échéance, sondage sur
  14 mois). Un instrument codé en dur devient périmé : `AddOnConfig.DefaultInstrument` est vide
  volontairement.
- `TradingEngine` déplace **tous** les stops/targets de protection, jamais un seul au hasard, et
  refuse un ordre limite sans flux de prix (`NO_MARKET_DATA`) — sinon la limite partirait à
  quelques ticks de 0 et s'exécuterait instantanément au marché.
- Les ordres limites sont émis en `Day` : une limite oubliée ne doit pas s'exécuter dans une
  session ultérieure.
- La publication d'état tourne toutes les 500 ms. Tout ce qui s'y trouve doit être en `Trace`
  (voir `SdLogger.Trace` / `TraceEvent`) et tout avertissement récurrent doit être limité en
  fréquence, sinon le fichier de log du jour devient inexploitable.

## Vérifier un changement

```bash
dotnet build "src/NinjaTrader.AddOn.StreamDeck/NinjaTrader.AddOn.StreamDeck.csproj" -c Release
```

La compilation ne prouve rien du comportement dans NinjaTrader. Pour tester réellement du code
qui ne dépend pas des API NT8 (comme `SdLogger` ou `SimpleJson`), compiler le fichier concerné
dans un petit projet console `net48` du scratchpad et l'exécuter — c'est ainsi qu'a été validé le
système de logs.
