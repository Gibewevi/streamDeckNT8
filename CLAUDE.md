# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

> Rédigé en français comme le reste de la documentation du projet ; le code et ses commentaires
> restent en anglais.

> **Ce projet s'étend sur DEUX dépôts.** Celui-ci est le moteur ; l'éditeur, le journal et les
> statistiques vivent dans `Bitlearn` (`C:\Users\pixel\Desktop\Bitlearn`). Six fichiers de ce dépôt
> y sont **copiés automatiquement** par `npm run build` — ne jamais les éditer là-bas.
>
> Si vous découvrez le projet, lire **`docs/carte-des-deux-depots.md`** avant tout le reste.
>
> Toute modification du moteur exige de **reconstruire l'installateur** :
> `docs/publier-une-version.md`. Sans ça, l'éditeur Bitlearn propose des réglages que le boîtier
> installé chez le trader ne sait pas appliquer.

## Vue d'ensemble

Surface de contrôle trading pour NinjaTrader 8 via Elgato Stream Deck. **Trois processus
séparés**, reliés par WebSocket JSON en localhost uniquement :

```
Hôte TradeDeck      ──ws://127.0.0.1:8218──►  Bridge  ──ws://127.0.0.1:8219──►  Add-On NT8
 Node.js + TS                                 C# .NET 8                         C# .NET 4.8
 src/deck-host/                               src/StreamDeckBridge/             src/NinjaTrader.AddOn.StreamDeck/
   │
   └──► http://127.0.0.1:8220 : interface de configuration (HTTP + WebSocket)
```

Le bridge est le **seul serveur du chemin de trading** : l'hôte et l'add-on sont tous deux des
clients qui s'y connectent et se reconnectent seuls. Il n'accepte qu'un client plugin et un
add-on à la fois.

**`src/deck-host/` (TradeDeck) remplace l'application Stream Deck d'Elgato ET le plugin.** Il
pilote le boîtier en USB directement, rend les touches lui-même (SVG → resvg → RGBA), sert sa
propre interface de configuration, et **lance le bridge** (`BridgeSupervisor`) — rôle que tenait
`plugin.ts`. `src/streamdeck-ninjatrader/` est l'ancien plugin Elgato, conservé pour référence :
**c'est `deck-host` qui fait foi**, ne pas modifier les deux en parallèle.

**Qui décide quoi** — c'est la clé pour savoir où corriger un bug :

| Décision | Composant | Fichier |
|----------|-----------|---------|
| Rendu des touches, appuis, layout, Auto BE, Auto TP/SL | Hôte | `src/deck-host/src/host.ts`, `visual-engine.ts` |
| Rendu des touches, appuis, réglages (ancien plugin) | Plugin | `src/plugin.ts` (tout y est, voir plus bas) |
| Validation, doublons, quantité, instrument/compte sélectionnés | Bridge | `MessageValidator`, `DuplicateGuard`, `StateManager` |
| Macro de sécurité, cooldown — **refus avant tout envoi d'ordre** | Bridge | `SafetyMacro`, `StateManager.IsOrderBlocked` |
| Résolution compte/instrument/position, envoi réel des ordres | Add-On | `ContextResolver`, `TradingEngine` |

`MessageRouter.ProcessPluginCommand` est le point de passage obligé de toute commande :
valide → anti-doublon → vérifie NT8 connecté → actions locales (`LocalActions`, traitées sans
NT8) → macro de sécurité → cooldown → enrichit et transmet à l'add-on.

**Flux d'état** (sens inverse) : l'add-on publie son état toutes les 500 ms → le bridge le fusionne
avec ce qu'il possède (quantité, instrument, sécurité) → il diffuse au client **toutes les 200 ms**
(`BridgeConfig.StateUpdateIntervalMs`, abaissé de 2 s pour que l'affichage suive un fill).
Ces deux boucles tournent en permanence : **ne jamais y ajouter de log en `INFO`** (voir Logs) —
à cette cadence, un `INFO` produit des centaines de milliers de lignes par jour.

## Commandes

```bash
# Bridge
dotnet build "src/StreamDeckBridge/StreamDeckBridge.csproj" -c Release
dotnet publish "src/StreamDeckBridge/StreamDeckBridge.csproj" -c Release -o src/StreamDeckBridge/publish

# Add-On NT8 (compile uniquement pour vérifier — voir « Déploiement »)
dotnet build "src/NinjaTrader.AddOn.StreamDeck/NinjaTrader.AddOn.StreamDeck.csproj" -c Release

# Indicateurs et stratégies NinjaScript (vérification de compilation — voir docs/strategie-structure-marche.md)
dotnet build "src/NinjaTrader.Scripts/NinjaTrader.Scripts.csproj" -c Release

# Hôte TradeDeck — c'est celui-ci qu'il faut construire
cd src/deck-host && npm run build       # tsc → dist/
cd src/deck-host && npx tsc --noEmit    # vérification de types seule
cd src/deck-host && npm run build:all   # bridge (publish → bridge/) puis hôte
cd src/deck-host && npm start           # node dist/host.js

# Ancien plugin Elgato — conservé pour référence, ne fait plus foi
cd src/streamdeck-ninjatrader && npm run build     # tsc → dist/
cd src/streamdeck-ninjatrader && npx tsc --noEmit   # vérification de types seule
```

**Il n'existe aucun test automatisé.** `docs/test-plan.md` est un plan de test **manuel**. Pour
valider un changement, il faut le faire tourner (voir ci-dessous), pas seulement le compiler.

### Faire tourner le bridge sans perturber l'installation active

Le bridge déployé écoute déjà sur 8218/8219 quand Stream Deck tourne. Toujours utiliser des
ports et des chemins d'état isolés pour un test :

```powershell
$env:SDBRIDGE_PluginPort = "9318"; $env:SDBRIDGE_AddonPort = "9319"
$env:SDBRIDGE_LogDirectory = "<scratchpad>\logtest"
$env:SDBRIDGE_SafetyStatePath = "<scratchpad>\safety.json"
$env:SDBRIDGE_SessionStatePath = "<scratchpad>\session.json"
```

Toute propriété de `BridgeConfig` est surchargeable par `SDBRIDGE_<NomDeLaPropriété>`.

> **Ne jamais sonder le port 8218** (`Test-NetConnection`, `curl`, `websocat`…) pendant que
> Stream Deck tourne : la connexion reste ouverte et la commande se bloque.

## Déploiement

L'installation active **ne correspond pas** aux instructions du README :

- l'add-on NT8 est déployé **en sources `.cs` à plat** dans
  `Documents\NinjaTrader 8\bin\Custom\AddOns\StreamDeck\`. Le DLL construit localement ne sert
  qu'à vérifier la compilation. **Depuis 0.17.0 l'installateur dépose ces sources**, avec
  `TdSwingEngine.cs` dans `Indicators\` : le poste de développement reste le seul endroit où
  l'on copie ces fichiers à la main ;

  > **Déposer les sources NinjaTrader OUVERT, toujours.** Il surveille `bin\Custom` tant qu'il
  > tourne : les fichiers arrivent, il recompile et recharge l'add-on de lui-même, sans même
  > redémarrer — vérifié le 20/08/2026, même PID avant et après le rechargement.
  >
  > **Fermé, il ne verra jamais ces fichiers arriver.** Il chargera son `NinjaTrader.Custom.dll`
  > précédent au lancement suivant, et le relancer n'y changera rien : il faut alors compiler à
  > la main, *Control Center → New → NinjaScript Editor → F5*. C'est ce qui a bloqué un client,
  > à qui on demandait justement de redémarrer. Preuve du mécanisme : le DLL du 12/08 ne
  > contenait pas `JournalSeal`, arrivé dans les sources le 13/08, alors que l'add-on tournait —
  > il tournait sur la compilation du 12.
- **l'hôte TradeDeck** s'installe dans `%LOCALAPPDATA%\TradeDeck` via
  `src/deck-host/packaging/install.ps1` : il y copie `dist/`, `ui/`, `node_modules/`, `bridge/`
  et un `node.exe`, enregistre une tâche planifiée (démarrage à l'ouverture de session, relance
  sur sortie non nulle) et désactive le démarrage automatique de Stream Deck, qui se disputerait
  le boîtier. Réversible par `uninstall.ps1` ;
- le bridge est livré **dans le dossier de l'hôte** (`…\TradeDeck\bridge\`), lancé par
  `BridgeSupervisor`. L'ancien emplacement `…\com.trader.ninjatrader.sdPlugin\bridge\` reste
  reconnu en repli.

Procédure complète et vérifiée : `.claude/skills/deploy/SKILL.md` (ou `/deploy`).

## Logs

Système de logs unifié aux trois composants, **un fichier par jour et par composant**, dans
`%APPDATA%\StreamDeckTrader\logs\` (`plugin-`, `bridge-`, `addon-AAAA-MM-JJ.log`). Format,
niveaux, catégories et scénarios de diagnostic : `docs/logging-strategy.md`.

Pour diagnostiquer un problème signalé par l'utilisateur, **lire ces fichiers d'abord** : ils
contiennent l'appui de touche, la commande, le refus ou l'ordre rejeté, corrélés par `requestId`.

Règles en ajoutant un log :
- niveau `TRACE` obligatoire pour tout ce qui est dans une boucle périodique (publication d'état
  toutes les 500 ms, diffusion toutes les 2 s, rafraîchissement de touches). Un `INFO` à cet
  endroit produit des centaines de milliers de lignes par jour ;
- ne journaliser un visuel de touche qu'**au changement** (`lastVisualSignature` dans `plugin.ts`) ;
- un avertissement répétitif doit être limité en fréquence (voir `_lastPnlWarning`,
  `_consecutiveSkips` dans `StatePublisher`) ;
- utiliser les helpers structurés (`log.event/eventWarn/fail`, `SdLogger.Event/EventWarn/Fail`)
  plutôt que les niveaux bruts : ils ajoutent la catégorie et le contexte clé=valeur.

## Pièges à connaître

- **Tout texte affiché sur une touche part dans un SVG : il DOIT être échappé.** `renderButtonSvg`
  (`deck-host/src/visuals.ts`) applique `esc()` sur chaque interpolation de texte. Un `&` ou un
  `<` non échappé rend le SVG impossible à parser, resvg lève, et la touche ne s'affiche plus —
  un libellé « S&P » suffisait. Toute nouvelle interpolation doit passer par `esc()`.
- **Ne jamais lire un entier JSON avec `GetInt32()` côté bridge — utiliser `TryGetInt32`.**
  `GetInt32()` **lève** sur `2.5`, mais aussi sur `2.0` et sur tout dépassement d'`int`. Une
  exception dans le chemin de validation coûtait la session plugin entière, que l'hôte
  reconnectait aussitôt pour la refaire tomber : boucle permanente. Une valeur malformée doit se
  lire comme absente, jamais comme une exception.
- **Une erreur de rendu n'est pas une perte de boîtier.** Dans `DeckDevice.paint`, le rastérisage
  est isolé de l'écriture USB : seule cette dernière déclenche `#handleLoss()`. Les confondre
  transformait une touche mal libellée en boucle de déconnexion/reconnexion.
- **`plugin.ts` contient toute la logique de l'ANCIEN plugin** (actions, visuels, état, sécurité).
  Les fichiers `src/actions/*.ts` sauf `status-action.ts`, ainsi que `services/display-adapter.ts`,
  ne sont **importés nulle part** : les modifier n'a aucun effet. Ce dossier ne fait plus foi —
  voir `src/deck-host/`.
- **`ClientWebSocket` (.NET 4.8) n'accepte qu'un seul `SendAsync` à la fois.** Un envoi concurrent
  abort la socket et fait perdre des confirmations d'ordre. D'où `_sendLock` dans le `BridgeClient`
  de l'add-on — tout nouvel envoi doit passer par `SendAsync`.
- **Compiler l'add-on produit ~180 avertissements CS0436** (types déjà présents dans
  `NinjaTrader.Custom.dll`). C'est normal, pas un problème à corriger.
- **`Account.Submit` est asynchrone** : un retour sans exception ne veut pas dire que l'ordre est
  accepté. Les rejets arrivent plus tard via `OrderMonitor` → événement `orderUpdate`.
- **La macro de sécurité ne peut pas être désarmée avant l'expiration du verrou**, par conception.
  Un refus `configureSafety`/`toggleSafety` pendant un verrou est le comportement attendu.
- **Toute règle de sécurité est scopée à un COMPTE — et ce compte se persiste.** `GuardEnforcer`
  n'inspecte que les comptes auxquels il est abonné, et le plafond de la macro se calcule contre la
  position du seul compte suivi. Le compte sélectionné est aussi le **maître du copieur**
  (`BridgeServer` : `master = state.Account`). Le 21/08/2026 il n'était pas persisté : après un
  redémarrage l'add-on est reparti sur le premier compte listé par NinjaTrader, l'enforcement
  surveillait un compte que personne ne tradait, le maître et son suiveur étaient intervertis — et
  le boîtier affichait `SAFETY:MAX` en rouge pendant une heure d'ordres passés à la souris. Le
  fichier `session.json` porte désormais `instrument` **et** `account`, et le bridge repousse
  `setAccount` avant `setInstrument` à chaque connexion de l'add-on. Ne jamais ajouter un réglage
  de sécurité scopé à un compte sans se demander ce qu'il devient au redémarrage.
- **`allowLiveAccounts` vaut `true` par défaut** : les comptes réels sont autorisés sans réglage.
  C'est le seul filtre entre Sim et réel — le vérifier avant tout test qui envoie des ordres.
- **`docs/architecture.md` a dérivé** sur plusieurs points (absence de persistance de l'état).
  En cas de contradiction, le code et `docs/protocol.md` font foi.
- **L'interface de configuration (8220) pilote l'envoi d'ordres indirectement** : elle écrit le
  layout et pousse les limites de sécurité au bridge. Sa WebSocket contrôle l'en-tête `Origin`
  — une page web quelconque pouvait sinon réécrire le layout et désactiver la macro de sécurité.
  Ne pas retirer ce contrôle.
- **Les artefacts de build sont versionnés** (`bin/`, `obj/`, et `dist/` de l'ancien plugin) : une
  modification de source fait apparaître des dizaines de fichiers binaires dans `git status`.
  C'est attendu ; ne pas « nettoyer » ces fichiers sans demander. Le `.gitignore` ne couvre que ce
  qui est régénérable et volumineux — `src/deck-host/node_modules/`, `dist/` et `bridge/` de
  l'hôte, et les dossiers `publish/`. Il ne s'applique pas aux fichiers déjà suivis, d'où la note
  explicite dans le fichier.

## Conventions

- Les commentaires expliquent **pourquoi**, pas quoi — en particulier devant chaque garde-fou :
  la raison est presque toujours un incident réel de trading. Ne pas les supprimer en refactorisant.
- Toute nouvelle règle de refus doit renvoyer un `code` d'erreur explicite (voir
  `docs/protocol.md`) : le plugin l'affiche sur la touche et le log s'en sert.
- Add-on : C# 9, .NET Framework 4.8, **aucune dépendance externe à l'exécution** — NinjaScript
  compile les sources telles quelles. C'est la raison d'être de `Utilities/SimpleJson.cs` ;
  ne pas y introduire Newtonsoft ni de package NuGet.
