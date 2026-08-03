# Protocole de communication V1

## Format de message universel

Tous les messages échangés entre les 3 composants utilisent le même enveloppe JSON :

```json
{
  "type": "command | response | event | error",
  "version": "1.0",
  "requestId": "uuid-v4",
  "timestamp": "2025-06-15T14:30:00.123Z",
  "source": "plugin | bridge | addon",
  "action": "actionName",
  "payload": { },
  "result": { },
  "error": {
    "code": "ERROR_CODE",
    "message": "Human-readable message"
  }
}
```

### Types de messages

| Type | Direction | Description |
|------|-----------|-------------|
| `command` | Plugin → Bridge → Add-On | Action à exécuter |
| `response` | Add-On → Bridge → Plugin | Résultat d'une commande |
| `event` | Add-On → Bridge → Plugin | Changement d'état asynchrone |
| `error` | Tout → Tout | Erreur structurée |

## Commandes V1

### Entrée — Ordres

#### `buyMarket`
```json
{
  "type": "command",
  "action": "buyMarket",
  "payload": {
    "account": "Sim101",
    "instrument": "ES 06-25",
    "quantity": 2
  }
}
```

#### `sellMarket`
```json
{
  "type": "command",
  "action": "sellMarket",
  "payload": {
    "account": "Sim101",
    "instrument": "ES 06-25",
    "quantity": 2
  }
}
```

#### `buyLimit`
```json
{
  "type": "command",
  "action": "buyLimit",
  "payload": {
    "account": "Sim101",
    "instrument": "ES 06-25",
    "quantity": 2,
    "offsetTicks": -2
  }
}
```
> `offsetTicks` : nombre de ticks depuis le dernier prix. Négatif = en dessous (typique pour un buy limit). L'Add-On calcule le prix réel.

#### `sellLimit`
```json
{
  "type": "command",
  "action": "sellLimit",
  "payload": {
    "account": "Sim101",
    "instrument": "ES 06-25",
    "quantity": 2,
    "offsetTicks": 2
  }
}
```

### Gestion de position

#### `flatten`
```json
{
  "type": "command",
  "action": "flatten",
  "payload": {
    "account": "Sim101",
    "instrument": "ES 06-25"
  }
}
```

#### `cancelOrders` — « Close All » (destructif)
⚠️ Annule **tous** les ordres en attente **et ferme la position** (appelle `Account.Flatten`).
C'est l'action derrière la touche « Close All ». Pour n'annuler que les ordres, utiliser
`cancelWorkingOrders`.
```json
{
  "type": "command",
  "action": "cancelOrders",
  "payload": {
    "account": "Sim101",
    "instrument": "ES 06-25"
  }
}
```

#### `cancelWorkingOrders` — annule les ordres seulement
Annule les ordres actifs de l'instrument **sans toucher à la position**. Idempotent :
succès avec `ordersCancelled: 0` s'il n'y a rien à annuler.
```json
{
  "type": "command",
  "action": "cancelWorkingOrders",
  "payload": {
    "account": "Sim101",
    "instrument": "ES 06-25"
  }
}
```

#### `reverse`
Annule d'abord les ordres de protection de la position en cours, puis envoie un ordre au
marché de `2 × quantité` pour retourner. Sans cette annulation, le stop de l'ancienne
position resterait actif et viendrait s'ajouter à la nouvelle au lieu de la protéger.
```json
{
  "type": "command",
  "action": "reverse",
  "payload": {
    "account": "Sim101",
    "instrument": "ES 06-25"
  }
}
```
> Reverse = flatten + ouverture en sens opposé avec la même quantité que la position aplatie.

#### `breakeven`
```json
{
  "type": "command",
  "action": "breakeven",
  "payload": {
    "account": "Sim101",
    "instrument": "ES 06-25",
    "offsetTicks": 0
  }
}
```
> `offsetTicks: 0` = BE simple. `offsetTicks: 2` = BE + 2 ticks.

#### `moveStop`
```json
{
  "type": "command",
  "action": "moveStop",
  "payload": {
    "account": "Sim101",
    "instrument": "ES 06-25",
    "deltaTicks": -1
  }
}
```
> `deltaTicks` : positif = éloigne le stop du prix (plus de marge), négatif = rapproche du prix (plus serré). La direction est relative au sens de la position.

#### `moveTarget`
```json
{
  "type": "command",
  "action": "moveTarget",
  "payload": {
    "account": "Sim101",
    "instrument": "ES 06-25",
    "deltaTicks": 1
  }
}
```

### Quantité

#### `qtySet`
```json
{
  "type": "command",
  "action": "qtySet",
  "payload": {
    "quantity": 5
  }
}
```

#### `qtyAdjust`
```json
{
  "type": "command",
  "action": "qtyAdjust",
  "payload": {
    "delta": 1
  }
}
```
> `delta: 1` = qty +1, `delta: -1` = qty -1

#### `qtyReset`
```json
{
  "type": "command",
  "action": "qtyReset",
  "payload": {}
}
```

### Instrument

#### `setInstrument`
```json
{
  "type": "command",
  "action": "setInstrument",
  "payload": {
    "instrument": "NQ 06-25"
  }
}
```

### Macro de sécurité verrouillable

Ces 4 actions sont traitées **localement par le bridge** (jamais transmises à NT8).
Chaque réponse — succès ou refus — embarque l'état complet de la macro dans `payload`
(voir `safety` dans `stateUpdate`), ce qui permet au plugin de rafraîchir ses touches
en un seul aller-retour.

#### `armSafety`
Arme la macro et démarre le verrou. Idempotent si déjà armée (un double appui
ne prolonge jamais un verrou en cours).
```json
{
  "type": "command",
  "action": "armSafety",
  "payload": {}
}
```

#### `disarmSafety`
Désarme la macro. **Refusé avec `SAFETY_MACRO_LOCKED` tant que le verrou court** —
il n'existe volontairement aucun paramètre de contournement.
```json
{
  "type": "command",
  "action": "disarmSafety",
  "payload": {}
}
```

#### `toggleSafety`
Arme si désarmée, tente de désarmer si armée. C'est l'action derrière la touche
Stream Deck « Safety Macro ».
```json
{
  "type": "command",
  "action": "toggleSafety",
  "payload": {}
}
```

#### `configureSafety`
Modifie les règles. **Refusé avec `SAFETY_MACRO_LOCKED` tant que la macro est armée** :
les limites ne peuvent pas être assouplies en cours de session. Au moins un champ est
requis, les champs absents sont laissés inchangés.
```json
{
  "type": "command",
  "action": "configureSafety",
  "payload": {
    "maxTradesWhenLosing": 15,
    "dailyLossLimit": 300,
    "lockDurationHours": 6
  }
}
```

| Champ | Plage | Description |
|-------|-------|-------------|
| `maxTradesWhenLosing` | 0–999 | Trades max une fois le PnL de session négatif. `0` = règle désactivée |
| `dailyLossLimit` | 0–1 000 000 | Perte de session max (nombre positif). `0` = règle désactivée |
| `lockDurationHours` | 0.05–24 | Durée du verrou après armement. Défaut `6` |

### Temporisation

#### `toggleCooldown`
Active ou désactive la temporisation. Désactiver annule une temporisation en cours.
```json
{
  "type": "command",
  "action": "toggleCooldown",
  "payload": {}
}
```

#### `configureCooldown`
Fixe la durée appliquée après un trade perdant. Le champ est **obligatoire** et doit être un
entier ; une décimale est refusée avec `INVALID_PAYLOAD`.

Une temporisation **déjà en cours conserve son échéance** : la raccourcir en séance donnerait
un moyen de lever la pause qu'on venait de demander, en modifiant un réglage.

Le bridge ne persiste pas cette valeur — l'hôte la repousse à chaque reconnexion, comme les
limites de sécurité. Sans envoi, le bridge applique `DefaultCooldownSeconds` (60 s).
```json
{
  "type": "command",
  "action": "configureCooldown",
  "payload": { "cooldownSeconds": 300 }
}
```

| Champ | Plage | Description |
|-------|-------|-------------|
| `cooldownSeconds` | 1–3600 | Durée de blocage des entrées après une perte. Défaut `60` |

L'état publié distingue les deux notions : `cooldownSeconds` est la durée **configurée**,
`cooldownSecondsRemaining` le **décompte** de la temporisation en cours.

### État

#### `getState`
```json
{
  "type": "command",
  "action": "getState",
  "payload": {}
}
```

## Réponses

### Réponse succès
```json
{
  "type": "response",
  "version": "1.0",
  "requestId": "same-uuid-as-command",
  "timestamp": "...",
  "source": "addon",
  "action": "buyMarket",
  "result": {
    "success": true,
    "orderId": "NT-12345",
    "message": "Buy 2 ES 06-25 Market submitted"
  }
}
```

### Réponse erreur
```json
{
  "type": "response",
  "version": "1.0",
  "requestId": "same-uuid-as-command",
  "timestamp": "...",
  "source": "addon",
  "action": "breakeven",
  "result": {
    "success": false
  },
  "error": {
    "code": "NO_POSITION",
    "message": "No open position for ES 06-25 on Sim101"
  }
}
```

## Événements (Add-On → Plugin)

### `stateUpdate`
Envoyé périodiquement (toutes les 500ms) et à chaque changement significatif.

```json
{
  "type": "event",
  "version": "1.0",
  "requestId": null,
  "timestamp": "...",
  "source": "addon",
  "action": "stateUpdate",
  "payload": {
    "connected": true,
    "account": {
      "name": "Sim101",
      "connected": true,
      "realizedPnl": -120.00,
      "unrealizedPnl": 275.00,
      "pnlAvailable": true
    },
    "instrument": {
      "name": "ES 06-25",
      "lastPrice": 5425.50,
      "tickSize": 0.25,
      "pointValue": 50.0
    },
    "position": {
      "exists": true,
      "direction": "Long",
      "quantity": 2,
      "averagePrice": 5420.00,
      "unrealizedPnl": 275.00,
      "hasStopOrder": true,
      "stopPrice": 5415.00,
      "stopOrderCount": 1,
      "hasTargetOrder": true,
      "targetPrice": 5435.00,
      "targetOrderCount": 1
    },
    "quantity": 5
  }
}
```

Le bridge enrichit cet événement avant de le relayer au plugin et y ajoute l'état de la
macro de sécurité :

```json
{
  "safety": {
    "armed": true,
    "locked": true,
    "lockSecondsRemaining": 19840,
    "lockDurationHours": 6,
    "maxTradesWhenLosing": 15,
    "dailyLossLimit": 300,
    "tradeCount": 13,
    "sessionPnl": -145.50,
    "pnlAvailable": true,
    "entriesBlocked": false,
    "blockReason": "",
    "tradingDay": "2026-07-29"
  }
}
```

| Champ | Description |
|-------|-------------|
| `armed` | Macro active |
| `locked` | Verrou en cours — la macro ne peut pas être désarmée |
| `lockSecondsRemaining` | Secondes restantes avant déverrouillage automatique |
| `tradeCount` | Trades ouverts depuis le début de `tradingDay` |
| `sessionPnl` | PnL depuis le PnL de début de journée (réalisé + latent) |
| `pnlAvailable` | `false` si NT8 n'expose pas le PnL du compte — les règles PnL sont alors inertes |
| `entriesBlocked` | Les ouvertures de position sont actuellement refusées |
| `blockReason` | `""`, `"dailyLoss"` ou `"tradeLimit"` |

### `orderUpdate`
Émis par l'add-on quand NinjaTrader **refuse** un ordre. `Account.Submit` est asynchrone :
sans cet événement, la touche clignote « OK » pour un ordre qui n'a jamais atteint le marché.

```json
{
  "type": "event",
  "version": "1.0",
  "requestId": null,
  "timestamp": "...",
  "source": "addon",
  "action": "orderUpdate",
  "payload": {
    "orderId": "NT-12345",
    "orderState": "Rejected",
    "rejected": true,
    "error": "OrderRejected",
    "reason": "Insufficient margin",
    "quantity": 2,
    "orderType": "Market",
    "orderAction": "Buy",
    "instrument": "ES 06-25"
  }
}
```

Le plugin affiche `REJECTED` pendant 5 s sur les touches d'entrée et déclenche un
`showAlert`. Le fond de la touche reste normal : un fond grisé signifie « bloqué », jamais
« rejeté ».

### `connectionStatus`
```json
{
  "type": "event",
  "version": "1.0",
  "requestId": null,
  "timestamp": "...",
  "source": "bridge",
  "action": "connectionStatus",
  "payload": {
    "bridgeRunning": true,
    "ntConnected": true,
    "pluginConnected": true
  }
}
```

## Codes d'erreur

| Code | Signification |
|------|---------------|
| `NT_DISCONNECTED` | NinjaTrader non connecté au bridge |
| `ACCOUNT_NOT_FOUND` | Compte introuvable dans NT8 |
| `INSTRUMENT_NOT_FOUND` | Instrument introuvable dans NT8 |
| `NO_POSITION` | Aucune position ouverte (requis par l'action) |
| `NO_STOP_ORDER` | Aucun ordre stop trouvé |
| `NO_MARKET_DATA` | Pas de flux de prix / tick size invalide — impossible de calculer un prix |
| `INVALID_STOP_PRICE` | Le prix demandé mettrait le stop/target du mauvais côté du marché |
| `NO_TARGET_ORDER` | Aucun ordre target trouvé |
| `INVALID_QUANTITY` | Quantité ≤ 0 |
| `INVALID_PAYLOAD` | Payload mal formé |
| `UNSUPPORTED_ACTION` | Action inconnue |
| `UNSUPPORTED_VERSION` | Version de protocole non supportée |
| `DUPLICATE_REQUEST` | requestId déjà traité récemment |
| `QUEUE_FULL` | File d'attente du bridge saturée |
| `LIVE_ACCOUNT_BLOCKED` | Compte réel bloqué par safe mode |
| `SAFETY_DAILY_LOSS_REACHED` | Perte journalière max atteinte — ouverture refusée par la macro |
| `SAFETY_TRADE_LIMIT_REACHED` | Nombre max de trades en perte atteint — ouverture refusée par la macro |
| `SAFETY_MACRO_LOCKED` | Désarmement / reconfiguration impossible avant la fin du verrou |
| `COOLDOWN_ACTIVE` | Cooldown actif après un trade perdant |
| `CONTEXT_MISSING` | Contexte insuffisant pour résoudre l'action |
| `ORDER_REJECTED` | NT8 a rejeté l'ordre |
| `INTERNAL_ERROR` | Erreur interne inattendue |
