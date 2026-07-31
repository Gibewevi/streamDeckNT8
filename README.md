# NinjaTrader 8 × Stream Deck — Trading Cockpit V1

Surface de contrôle trading spécialisée pour NinjaTrader 8 via Elgato Stream Deck.
Inspiré de la philosophie UX ProRealTime : boutons métier, feedback en temps réel, gestion rapide.

## Architecture

```
Stream Deck Plugin ←→ Bridge (WebSocket hub) ←→ NinjaTrader 8 Add-On
   (Node.js)           (C# .NET 8)                (C# .NET 4.8)
   port 8218           central                     port 8219
```

Les 3 composants communiquent par **WebSocket JSON** en localhost uniquement.

## Fonctionnalités V1

### Entrée
- Buy Market / Sell Market
- Buy Limit / Sell Limit (offset en ticks depuis le dernier prix)

### Gestion de position
- Flatten (aplatir toute la position)
- **Close All** — annule tous les ordres **et** ferme la position (destructif)
- **Cancel Orders** — annule les ordres en attente **sans** toucher à la position
- Reverse (retourner la position — annule d'abord les protections de l'ancienne)
- Break-even simple
- Break-even + offset configurable (ex: BE +2 ticks)
- Move stop par pas de N ticks
- Move target par pas de N ticks

### Quantité
- Presets rapides (1, 2, 5, 10, 20...)
- Incrément / décrément (+1 / -1)
- Reset à la valeur par défaut

### Instruments
- Changement d'instrument mono-instrument global
- Boutons par instrument (ES, NQ, CL...)

### Macro de sécurité verrouillable
- Touche **Safety Macro** : armement avant la session, verrou de 6h par défaut
- **Impossible à désactiver avant la fin du verrou** — aucun déverrouillage manuel
- Limite de trades quand le compte est en perte (défaut : 15)
- Perte journalière max (défaut : 300 $, mesurée depuis le PnL de début de journée)
- Blocage **avant l'envoi de l'ordre** : une touche refusée ne produit aucun ordre
- Les sorties (Close, Cancel, BE, Move stop/target) restent toujours disponibles

### Garde-fous d'exécution
- Ordre limite **refusé** si le flux de prix est absent (`NO_MARKET_DATA`) — sinon la limite
  partirait à quelques ticks de 0, et une limite vendeuse sous le marché s'exécuterait
  instantanément au marché
- Break-even **refusé** avec un message explicite si le trade n'est pas encore en profit
- Rejets NinjaTrader (marge, marché fermé…) remontés sur les touches : `Account.Submit` étant
  asynchrone, un « OK » ne signifiait pas que l'ordre avait été accepté
- Move Stop / Move Target déplacent **tous** les ordres de protection, pas un au hasard
- Ordres limites en `Day` : une limite oubliée ne s'exécute pas dans une session ultérieure
- Envois WebSocket sérialisés côté add-on : un envoi concurrent abortait la socket et
  faisait perdre des confirmations d'ordre

### Statut / Feedback
- Compte actif
- Instrument actif
- Position (direction + quantité)
- P&L non réalisé
- Quantité courante
- État de connexion Bridge / NT8

## Installation

### 1. Bridge

```bash
cd src/StreamDeckBridge
dotnet build -c Release
dotnet run
```

Le bridge écoute sur :
- `ws://127.0.0.1:8218` (plugin SD)
- `ws://127.0.0.1:8219` (NT8 add-on)

### 2. NinjaTrader 8 Add-On

1. Ouvrir `NinjaTrader.AddOn.StreamDeck.csproj` dans Visual Studio
2. Ajouter les références aux DLLs NinjaTrader si les chemins diffèrent :
   - `C:\Program Files\NinjaTrader 8\bin\NinjaTrader.Core.dll`
   - `C:\Program Files\NinjaTrader 8\bin\NinjaTrader.Gui.dll`
   - `C:\Program Files\NinjaTrader 8\bin\NinjaTrader.Client.dll`
3. Compiler en Release
4. Copier le DLL résultant dans : `Documents\NinjaTrader 8\bin\Custom\AddOns\`
5. Redémarrer NinjaTrader 8 — l'add-on démarre automatiquement

### 3. Stream Deck Plugin

```bash
cd src/streamdeck-ninjatrader
npm install
npm run build
```

Pour installer dans Stream Deck :
1. Copier le dossier `com.trader.ninjatrader.sdPlugin` dans :
   `%APPDATA%\Elgato\StreamDeck\Plugins\`
2. Copier le contenu de `dist/` dans le dossier `dist/` du plugin
3. Redémarrer Stream Deck

## Configuration

### Bridge (`BridgeConfig` dans Program.cs)
| Paramètre | Défaut | Description |
|-----------|--------|-------------|
| `PluginPort` | 8218 | Port WebSocket pour le plugin |
| `AddonPort` | 8219 | Port WebSocket pour l'add-on |
| `DefaultAccount` | Sim101 | Compte par défaut |
| `DefaultInstrument` | ES 06-25 | Instrument par défaut |
| `DefaultQuantity` | 1 | Quantité de départ |
| `AllowLiveAccounts` | false | **Safe mode** — interdit les comptes réels |
| `DefaultMaxTradesWhenLosing` | 15 | Macro de sécurité — trades max en perte (1ᵗ lancement) |
| `DefaultDailyLossLimit` | 300 | Macro de sécurité — perte journalière max (1ᵗ lancement) |
| `DefaultSafetyLockHours` | 6 | Macro de sécurité — durée du verrou (1ᵗ lancement) |
| `SafetyStatePath` | *(vide)* | Fichier d'état de la macro. Vide = `%APPDATA%\StreamDeckTrader\safety-macro.json` |

Chaque propriété est surchargeable par variable d'environnement préfixée `SDBRIDGE_`
(ex. `SDBRIDGE_PluginPort=9218`, `SDBRIDGE_SafetyStatePath=D:\safety.json`).

### Settings Stream Deck (par action)
Chaque action peut overrider le compte et l'instrument via le Property Inspector.

## Macro de sécurité verrouillable

Garde-fou que le trader s'impose **avant** sa session, et qu'il ne peut plus retirer
pendant la durée du verrou.

### Cycle de vie

1. **Désarmée** — les limites sont réglables dans le Property Inspector de la touche
   *Safety Macro* (trades max en perte, perte journalière, durée du verrou).
2. **Armement** — un appui sur la touche arme la macro et démarre le verrou (6h par défaut).
3. **Verrouillée** — la touche affiche le temps restant. Un appui tente un désarmement et
   est **refusé** (`SAFETY_MACRO_LOCKED`) ; les réglages sont également gelés.
4. **Expiration** — à la fin du verrou la macro se désarme automatiquement et les réglages
   redeviennent modifiables. Les paramètres sont conservés pour le prochain armement.

### Règles appliquées

| Règle | Déclenchement |
|-------|---------------|
| Trades max en perte | `tradeCount ≥ limite` **et** PnL de session négatif |
| Perte journalière | PnL de session `≤ -limite` (réalisé + latent, depuis le PnL de début de journée) |

Un « trade » est compté quand NinjaTrader signale un passage de *flat* à *position ouverte*
sur l'instrument suivi (renforcer une position existante ne compte pas comme un nouveau trade).
Le compteur et la référence PnL sont remis à zéro au changement de jour calendaire local.

Actions bloquées : `buyMarket`, `sellMarket`, `buyLimit`, `sellLimit`, `reverse`.
Actions **jamais** bloquées : `flatten`, `cancelOrders`, `breakeven`, `moveStop`, `moveTarget` —
le trader doit toujours pouvoir sortir de position.

### Garanties d'application

- Le blocage est évalué **dans le bridge, avant tout envoi vers NT8** : une touche refusée
  ne produit aucun ordre sur le marché, seulement un message d'erreur et un `showAlert`.
- L'état (paramètres, armement, échéance du verrou, compteur, référence PnL) est **persisté** :
  redémarrer le bridge, le plugin ou Stream Deck ne déverrouille pas la macro.
- Les règles PnL s'appuient sur le PnL du compte publié par l'add-on NT8. Si NinjaTrader ne
  l'expose pas, `pnlAvailable` passe à `false`, la touche affiche `PNL?` et les règles PnL
  sont inertes — à vérifier avant d'armer.

## Sécurité

- **Safe mode** activé par défaut : seuls les comptes `Sim*` sont autorisés
- **Macro de sécurité verrouillable** (ci-dessus) — blocage avant envoi de l'ordre
- Validation stricte des payloads à chaque couche
- Protection anti-doublons (requestId unique sur 60s)
- Aucune action si NT8 n'est pas connecté
- Aucune action dépendante d'une position si pas de position
- Aucune simulation de clavier/souris — API NinjaTrader native uniquement
- Communication localhost uniquement

## Logs

Tout est journalisé automatiquement, **un fichier par jour et par composant**, dans :

```
%APPDATA%\StreamDeckTrader\logs\
    plugin-AAAA-MM-JJ.log
    bridge-AAAA-MM-JJ.log
    addon-AAAA-MM-JJ.log
```

Chaque appui de touche, commande, ordre, refus, changement de position, connexion, erreur et
anomalie y figure avec un horodatage à la milliseconde et son contexte. Les trois fichiers
partagent le même format et le même `requestId`, ce qui permet de suivre une action de bout en
bout. Rétention : 30 jours.

Voir [docs/logging-strategy.md](docs/logging-strategy.md) pour le format, les niveaux et les
scénarios de diagnostic.

## Ordre de démarrage recommandé

1. Lancer le **Bridge** en premier
2. Lancer **NinjaTrader 8** (l'add-on se connecte auto)
3. Ouvrir **Stream Deck** (le plugin se connecte auto)

L'ordre n'est pas obligatoire : les reconnexions sont automatiques.

## Arborescence

```
stream deck/
├── StreamDeckTrader.sln
├── README.md
├── docs/
│   ├── architecture.md          # Architecture détaillée + justifications
│   ├── protocol.md              # Format des messages, commandes, événements
│   ├── test-plan.md             # Plan de test complet
│   └── logging-strategy.md      # Stratégie de logs
├── src/
│   ├── StreamDeckBridge/        # Bridge C# .NET 8
│   │   ├── Program.cs
│   │   ├── BridgeServer.cs      # Serveur WebSocket dual-port
│   │   ├── MessageRouter.cs     # Routage des messages
│   │   ├── MessageValidator.cs  # Validation des commandes
│   │   ├── StateManager.cs      # État partagé (qty, instrument)
│   │   ├── SafetyMacro.cs       # Macro de sécurité verrouillable (état persisté)
│   │   ├── DuplicateGuard.cs    # Anti-doublon requestId
│   │   └── Models/
│   ├── NinjaTrader.AddOn.StreamDeck/  # Add-On NT8 C# .NET 4.8
│   │   ├── StreamDeckAddOn.cs   # Point d'entrée
│   │   ├── Services/
│   │   │   ├── BridgeClient.cs       # WebSocket vers le bridge
│   │   │   ├── TradingEngine.cs      # Exécution des actions
│   │   │   ├── ContextResolver.cs    # Résolution compte/instrument/position
│   │   │   ├── CommandDispatcher.cs   # Dispatch des commandes
│   │   │   ├── OrderMonitor.cs        # Remonte les rejets d'ordres NT8
│   │   │   └── StatePublisher.cs     # Publication d'état
│   │   ├── Models/
│   │   └── Utilities/
│   └── streamdeck-ninjatrader/  # Plugin Stream Deck (Node.js/TS)
│       ├── package.json
│       ├── tsconfig.json
│       ├── src/
│       │   ├── plugin.ts             # Point d'entrée
│       │   ├── services/
│       │   │   └── bridge-client.ts
│       │   ├── actions/
│       │   │   ├── base-action.ts
│       │   │   ├── order-actions.ts
│       │   │   ├── position-actions.ts
│       │   │   ├── qty-actions.ts
│       │   │   ├── instrument-action.ts
│       │   │   └── status-action.ts
│       │   ├── models/
│       │   │   └── messages.ts
│       │   └── utils/
│       │       └── logger.ts
│       └── com.trader.ninjatrader.sdPlugin/
│           ├── manifest.json
│           └── ui/                    # Property Inspectors
```

## Limitations V1

1. **Mono-instrument global** — un seul instrument actif à la fois
2. **Pas de création de stop/target** — BE et Move ne fonctionnent que sur des stops existants
3. **Limit par offset** — pas de placement au bid/ask dynamique
4. **Pas de mode confirmation** — exécution immédiate
5. **Un seul Stream Deck** — pas de multi-plugin
6. **Pas de persistance** — la quantité est réinitialisée au redémarrage du bridge

## Pistes V2

- Multi-instrument par bouton (mode multi-instrument complet)
- Limit au Bid/Ask dynamique avec flux de prix
- Mode confirmation configurable
- Trailing stop
- OCO / bracket orders configurables
- Persistance d'état
- Multi-Stream Deck
- Support Stream Deck+ (encodeurs rotatifs)
- Dashboard P&L enrichi
- Macro-actions (séquences)
# streamDeckNT8
