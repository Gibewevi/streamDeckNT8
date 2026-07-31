# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

> Rédigé en français comme le reste de la documentation du projet ; le code et ses commentaires
> restent en anglais.

## Vue d'ensemble

Surface de contrôle trading pour NinjaTrader 8 via Elgato Stream Deck. **Trois processus
séparés**, reliés par WebSocket JSON en localhost uniquement :

```
Plugin Stream Deck  ──ws://127.0.0.1:8218──►  Bridge  ──ws://127.0.0.1:8219──►  Add-On NT8
 Node.js + TS                                 C# .NET 8                         C# .NET 4.8
 src/streamdeck-ninjatrader/                  src/StreamDeckBridge/             src/NinjaTrader.AddOn.StreamDeck/
```

Le bridge est le **seul serveur** : le plugin et l'add-on sont tous deux des clients qui s'y
connectent et se reconnectent seuls. Il n'accepte qu'un plugin et un add-on à la fois.

**Qui décide quoi** — c'est la clé pour savoir où corriger un bug :

| Décision | Composant | Fichier |
|----------|-----------|---------|
| Rendu des touches, appuis, réglages | Plugin | `src/plugin.ts` (tout y est, voir plus bas) |
| Validation, doublons, quantité, instrument/compte sélectionnés | Bridge | `MessageValidator`, `DuplicateGuard`, `StateManager` |
| Macro de sécurité, cooldown — **refus avant tout envoi d'ordre** | Bridge | `SafetyMacro`, `StateManager.IsOrderBlocked` |
| Résolution compte/instrument/position, envoi réel des ordres | Add-On | `ContextResolver`, `TradingEngine` |

`MessageRouter.ProcessPluginCommand` est le point de passage obligé de toute commande :
valide → anti-doublon → vérifie NT8 connecté → actions locales (`LocalActions`, traitées sans
NT8) → macro de sécurité → cooldown → enrichit et transmet à l'add-on.

**Flux d'état** (sens inverse) : l'add-on publie son état toutes les 500 ms → le bridge le fusionne
avec ce qu'il possède (quantité, instrument, sécurité) → il diffuse au plugin toutes les 2 s.
Ces deux boucles tournent en permanence : **ne jamais y ajouter de log en `INFO`** (voir Logs).

## Commandes

```bash
# Bridge
dotnet build "src/StreamDeckBridge/StreamDeckBridge.csproj" -c Release
dotnet publish "src/StreamDeckBridge/StreamDeckBridge.csproj" -c Release -o src/StreamDeckBridge/publish

# Add-On NT8 (compile uniquement pour vérifier — voir « Déploiement »)
dotnet build "src/NinjaTrader.AddOn.StreamDeck/NinjaTrader.AddOn.StreamDeck.csproj" -c Release

# Plugin Stream Deck
cd src/streamdeck-ninjatrader && npm run build     # tsc → dist/
cd src/streamdeck-ninjatrader && npx tsc --noEmit   # vérification de types seule
cd src/streamdeck-ninjatrader && npm run watch      # tsc --watch
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
  `Documents\NinjaTrader 8\bin\Custom\AddOns\StreamDeck\` — NinjaScript les compile au démarrage
  de NinjaTrader. Le DLL construit localement ne sert qu'à vérifier la compilation ;
- le bridge vit **dans le dossier du plugin**, en `…\com.trader.ninjatrader.sdPlugin\bridge\` ;
  `plugin.ts` le lance automatiquement depuis là.

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

- **`plugin.ts` contient toute la logique du plugin** (actions, visuels, état, sécurité). Les
  fichiers `src/actions/*.ts` sauf `status-action.ts`, ainsi que `services/display-adapter.ts`,
  ne sont **importés nulle part** : les modifier n'a aucun effet.
- **`ClientWebSocket` (.NET 4.8) n'accepte qu'un seul `SendAsync` à la fois.** Un envoi concurrent
  abort la socket et fait perdre des confirmations d'ordre. D'où `_sendLock` dans le `BridgeClient`
  de l'add-on — tout nouvel envoi doit passer par `SendAsync`.
- **Compiler l'add-on produit ~180 avertissements CS0436** (types déjà présents dans
  `NinjaTrader.Custom.dll`). C'est normal, pas un problème à corriger.
- **`Account.Submit` est asynchrone** : un retour sans exception ne veut pas dire que l'ordre est
  accepté. Les rejets arrivent plus tard via `OrderMonitor` → événement `orderUpdate`.
- **La macro de sécurité ne peut pas être désarmée avant l'expiration du verrou**, par conception.
  Un refus `configureSafety`/`toggleSafety` pendant un verrou est le comportement attendu.
- **`docs/architecture.md` a dérivé** sur plusieurs points (Safe Mode par défaut, absence de
  persistance de l'état). En cas de contradiction, le code et `docs/protocol.md` font foi.
- **Il n'y a pas de `.gitignore`** et les artefacts de build (`bin/`, `obj/`, `dist/`) sont
  versionnés : une modification de source fait apparaître des dizaines de fichiers binaires dans
  `git status`, et tout dossier de sortie créé (`publish/`…) y apparaît aussi. C'est attendu ;
  ne pas « nettoyer » ces fichiers sans demander.

## Conventions

- Les commentaires expliquent **pourquoi**, pas quoi — en particulier devant chaque garde-fou :
  la raison est presque toujours un incident réel de trading. Ne pas les supprimer en refactorisant.
- Toute nouvelle règle de refus doit renvoyer un `code` d'erreur explicite (voir
  `docs/protocol.md`) : le plugin l'affiche sur la touche et le log s'en sert.
- Add-on : C# 9, .NET Framework 4.8, **aucune dépendance externe à l'exécution** — NinjaScript
  compile les sources telles quelles. C'est la raison d'être de `Utilities/SimpleJson.cs` ;
  ne pas y introduire Newtonsoft ni de package NuGet.
