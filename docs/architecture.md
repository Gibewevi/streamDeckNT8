# NinjaTrader 8 × Stream Deck — Architecture Technique V1

## Vue d'ensemble

```
┌──────────────┐    WebSocket     ┌──────────────┐    WebSocket     ┌──────────────────┐
│  Stream Deck │◄───────────────►│    Bridge     │◄───────────────►│  NinjaTrader 8   │
│    Plugin    │   localhost:8218 │   (C# .NET)  │   localhost:8219│    Add-On        │
│  (Node.js)   │                 │               │                 │  (C# .NET 4.8)   │
└──────────────┘                 └──────────────┘                 └──────────────────┘
```

## Choix d'architecture — Justification

### 3 composants séparés

| Choix | Pourquoi |
|-------|----------|
| **Bridge central** plutôt que connexion directe | Découplage : le plugin SD ne dépend pas du cycle de vie de NT8. Le bridge peut tamponner, valider, logger avant transmission. Permet un redémarrage de NT8 sans perdre la session SD. |
| **WebSocket** plutôt que Named Pipes ou TCP brut | WebSocket est natif en Node.js (SD plugin) et en .NET (NT8 + bridge). Full-duplex, texte JSON, simple à debugger avec un outil comme websocat. Port unique par endpoint. |
| **JSON** plutôt que binaire | Lisibilité en debug, extensibilité, pas de contrainte de perf pour des messages < 1 Ko en local. |
| **Deux connexions WS** (plugin↔bridge, bridge↔NT) | Séparation nette des responsabilités. Le bridge gère les reconnexions de chaque côté indépendamment. |

### Technos par composant

| Composant | Techno | Raison |
|-----------|--------|--------|
| NinjaTrader Add-On | C# .NET Framework 4.8 | Imposé par NT8 |
| Bridge | C# .NET 8 console app | Même langage que l'Add-On, partage des modèles, exécution autonome |
| Stream Deck Plugin | Node.js + TypeScript | Imposé par Elgato SDK v6 |

## Ports par défaut

| Connexion | Port | Configurable |
|-----------|------|-------------|
| Plugin SD → Bridge | `ws://127.0.0.1:8218` | Oui (settings plugin + bridge config) |
| Bridge → NT8 Add-On | `ws://127.0.0.1:8219` | Oui (bridge config + Add-On config) |

> Les deux connexions sont **localhost only** — aucune exposition réseau.

## Flux de données

### Commande (Plugin → NT8)

```
[Bouton pressé]
  → Plugin: construit message Command JSON
    → WebSocket → Bridge
      → Bridge: valide, log, enrichit requestId si absent
        → WebSocket → NT8 Add-On
          → Add-On: résout contexte (compte, instrument, position)
            → Exécute via API NinjaTrader
              → Construit message Response
                → WebSocket → Bridge
                  → Bridge: log, transmet
                    → WebSocket → Plugin
                      → Plugin: met à jour le visuel du bouton
```

### Événement (NT8 → Plugin)

```
[Événement NT8 : position change, ordre rempli, etc.]
  → Add-On: construit message Event JSON
    → WebSocket → Bridge
      → Bridge: log, broadcast à tous les plugins connectés
        → WebSocket → Plugin(s)
          → Plugin: met à jour les boutons de statut
```

## Résolution de contexte

### Stratégie explicite

La V1 utilise un **contexte global unique** résolu ainsi :

1. **Compte** : défini dans les settings globaux du plugin (`accountName`, ex: `"Sim101"`). Transmis dans chaque commande. L'Add-On vérifie que ce compte existe.

2. **Instrument** : défini dans les settings globaux (`instrumentName`, ex: `"ES 06-25"`). Peut être changé dynamiquement par une action `setInstrument`. Transmis dans chaque commande.

3. **Position** : déterminée par le couple `(compte, instrument)`. L'Add-On interroge `Account.Positions` en temps réel. Pas de cache client — source de vérité = NT8.

4. **Quantité** : maintenue côté Bridge dans un `StateManager`. Initialisée au `defaultQuantity` du settings. Modifiable par `qtyAdjust`, `qtySet`, `qtyReset`. Transmise dans les commandes d'entrée.

5. **Ordres stop/target à modifier** : pour `moveStop`/`moveTarget`, l'Add-On cherche les ordres actifs liés à la position courante sur `(compte, instrument)`. S'il y en a plusieurs, stratégie V1 = modifier **le plus proche du prix actuel** (le plus conservateur). V2 pourra ajouter la sélection explicite.

### Priorités de résolution

```
Payload de la commande (override explicite)
  ↓ si absent
Settings globaux du plugin
  ↓ si absent
Valeurs par défaut du bridge
  ↓ si absent
Refus avec erreur CONTEXT_MISSING
```

## Sécurité & Garde-fous

### Validation en couches

| Couche | Validations |
|--------|-------------|
| **Plugin** | Format JSON, action connue, settings obligatoires présents |
| **Bridge** | Schema JSON complet, version supportée, requestId présent, account/instrument non vides, quantité > 0 |
| **Add-On** | Compte existe, instrument existe, connexion NT8 active, position existe (pour actions dépendantes), pas de double exécution (requestId idempotent sur 60s) |

### Safe Mode V1

- **Par défaut, seul le compte `Sim101` est autorisé.**
- Un flag `allowLiveAccounts` dans la config du bridge (défaut: `false`) doit être mis à `true` explicitement pour autoriser les comptes réels.
- Le bridge affiche un warning au démarrage si `allowLiveAccounts` est `true`.
- Chaque action sur un compte réel est loggée avec le niveau `WARNING`.

### Garde-fous spécifiques

| Situation | Comportement |
|-----------|-------------|
| NT8 non connecté | Refus immédiat, error `NT_DISCONNECTED` |
| Compte introuvable | Refus, error `ACCOUNT_NOT_FOUND` |
| Instrument introuvable | Refus, error `INSTRUMENT_NOT_FOUND` |
| Action position sans position | Refus, error `NO_POSITION` |
| Action stop sans stop existant | Refus, error `NO_STOP_ORDER` |
| Double requestId en < 60s | Refus, error `DUPLICATE_REQUEST` |
| Quantité ≤ 0 | Refus, error `INVALID_QUANTITY` |
| Bridge surchargé (queue > 50) | Refus, error `QUEUE_FULL` |

## Break-Even — Design détaillé

### BE Simple
1. Récupère la position sur `(compte, instrument)`
2. Récupère le prix moyen d'entrée (`AveragePrice`)
3. Cherche le ou les ordres stop actifs
4. Modifie le stop au prix moyen d'entrée
5. Si plusieurs stops : modifie **tous** les stops au BE (comportement le plus sûr)

### BE + Offset
- Même logique, mais le prix cible = `AveragePrice + (offset × tickSize)` pour un long, `AveragePrice - (offset × tickSize)` pour un short
- L'offset est configurable dans les settings de l'action (défaut: 2 ticks)

### Cas limites
| Cas | Comportement V1 |
|-----|-----------------|
| Position partiellement réduite | Le BE utilise le `AveragePrice` actuel de NT8 (qui reflète la position résiduelle) |
| Pas de stop existant | Refus avec `NO_STOP_ORDER` — V1 ne crée pas de stop, elle le déplace seulement |
| Stop déjà au-delà du BE | L'action est quand même exécutée (l'utilisateur peut vouloir ramener le stop au BE) |
| Prix BE invalide (ex: au-delà du marché) | NT8 refusera l'ordre — l'erreur est propagée au plugin |

## Pages Stream Deck V1

### Page 1 — Entrée / Exécution
```
┌─────────┬─────────┬─────────┬─────────┬─────────┐
│BUY MKT  │SELL MKT │BUY LMT  │SELL LMT │ FLATTEN │
│  vert   │  rouge  │ vert/dim│rouge/dim│  orange │
├─────────┼─────────┼─────────┼─────────┼─────────┤
│CANCEL   │REVERSE  │  BE     │ BE +2   │  QTY    │
│  jaune  │  violet │  bleu   │  bleu   │  [n]    │
├─────────┼─────────┼─────────┼─────────┼─────────┤
│STOP -1  │STOP +1  │TGT -1   │TGT +1   │  INFO   │
│  tick   │  tick   │  tick   │  tick   │  page→  │
└─────────┴─────────┴─────────┴─────────┴─────────┘
```

### Page 2 — Quantité
```
┌─────────┬─────────┬─────────┬─────────┬─────────┐
│  QTY 1  │  QTY 2  │  QTY 5  │  QTY 10 │  QTY 20 │
├─────────┼─────────┼─────────┼─────────┼─────────┤
│  QTY +1 │  QTY -1 │QTY RESET│         │  EXEC   │
│         │         │         │         │  page→  │
├─────────┼─────────┼─────────┼─────────┼─────────┤
│  [current qty display]      │         │  INFO   │
│                             │         │  page→  │
└─────────┴─────────┴─────────┴─────────┴─────────┘
```

### Page 3 — Instruments
```
┌─────────┬─────────┬─────────┬─────────┬─────────┐
│  ES     │  NQ     │  YM     │  CL     │  GC     │
├─────────┼─────────┼─────────┼─────────┼─────────┤
│  6E     │  6B     │  ZB     │  RTY    │  MES    │
├─────────┼─────────┼─────────┼─────────┼─────────┤
│ [active instrument]         │         │  EXEC   │
│                             │         │  page→  │
└─────────┴─────────┴─────────┴─────────┴─────────┘
```

### Page 4 — Statut / Info
```
┌─────────┬─────────┬─────────┬─────────┬─────────┐
│ ACCOUNT │INSTRUMNT│POSITION │  P&L    │  QTY    │
│ Sim101  │ ES 06-25│ +2 Long │ +$125   │   5     │
├─────────┼─────────┼─────────┼─────────┼─────────┤
│ BRIDGE  │ NT8     │  SAFE   │         │  EXEC   │
│   🟢    │   🟢    │  MODE   │         │  page→  │
├─────────┼─────────┼─────────┼─────────┼─────────┤
│         │         │         │         │         │
└─────────┴─────────┴─────────┴─────────┴─────────┘
```

## Limitations connues V1

1. **Mono-instrument global** : pas de multi-instrument par bouton (prévu V2)
2. **Pas de création de stop/target** : le BE et move stop ne fonctionnent que sur des stops déjà existants
3. **Buy/Sell Limit** : prix basé sur un offset depuis le dernier prix (pas de placement graphique)
4. **Pas de mode confirmation** : toutes les actions sont exécutées immédiatement
5. **Pas de trailing stop** : prévu V2
6. **Un seul Stream Deck connecté** : le bridge ne gère qu'un plugin client en V1
7. **Pas de persistance d'état** : la quantité est réinitialisée au redémarrage du bridge

## Pistes V2

- Multi-instrument par bouton / profil
- Mode confirmation configurable par action
- Trailing stop
- OCO / bracket configurable
- Limit au Bid/Ask dynamique (nécessite flux de prix)
- Persistance de l'état (quantité, dernier instrument)
- Multi-plugin / Multi-Stream Deck
- Dashboard PnL enrichi
- Alertes sonores / visuelles
- Macro-actions (séquences)
- Support Stream Deck+ (encodeurs rotatifs pour qty/stop)
