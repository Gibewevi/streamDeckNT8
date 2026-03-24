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

#### `cancelOrders`
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

#### `reverse`
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
      "connected": true
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
      "hasTargetOrder": true,
      "targetPrice": 5435.00
    },
    "quantity": 5
  }
}
```

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
| `NO_TARGET_ORDER` | Aucun ordre target trouvé |
| `INVALID_QUANTITY` | Quantité ≤ 0 |
| `INVALID_PAYLOAD` | Payload mal formé |
| `UNSUPPORTED_ACTION` | Action inconnue |
| `UNSUPPORTED_VERSION` | Version de protocole non supportée |
| `DUPLICATE_REQUEST` | requestId déjà traité récemment |
| `QUEUE_FULL` | File d'attente du bridge saturée |
| `LIVE_ACCOUNT_BLOCKED` | Compte réel bloqué par safe mode |
| `CONTEXT_MISSING` | Contexte insuffisant pour résoudre l'action |
| `ORDER_REJECTED` | NT8 a rejeté l'ordre |
| `INTERNAL_ERROR` | Erreur interne inattendue |
