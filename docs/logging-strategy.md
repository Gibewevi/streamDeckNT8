# Stratégie de Logs V1

## Principes

1. **Corrélation** : tout requestId est loggé à chaque couche pour permettre le suivi d'une action de bout en bout
2. **Lisibilité** : chaque log contient le contexte suffisant pour comprendre ce qui se passe sans aller lire un autre log
3. **Niveaux** : INFO pour les événements normaux, WARN pour les refus / anomalies, ERROR pour les exceptions
4. **Préfixes** : chaque composant a un préfixe distinct

## Format par composant

### Bridge
```
[timestamp] [LEVEL] [StreamDeckBridge] message
```
Exemple :
```
2025-06-15 14:30:00.123 [INFO] [StreamDeckBridge] [REQ:a1b2c3d4] Received command: buyMarket
2025-06-15 14:30:00.124 [INFO] [StreamDeckBridge] [REQ:a1b2c3d4] Forwarding buyMarket to NT8
2025-06-15 14:30:00.125 [WARN] [StreamDeckBridge] [REQ:e5f6g7h8] Validation failed: INVALID_QUANTITY - Quantity must be >= 1
```

### NinjaTrader Add-On
```
[StreamDeck] LEVEL | message
```
Exemple :
```
[StreamDeck] INFO  | [REQ:a1b2c3d4] Dispatching: buyMarket
[StreamDeck] INFO  | [REQ:a1b2c3d4] Buy 2 ES 06-25 Market submitted (OrderId: NT-12345)
[StreamDeck] WARN  | [REQ:x9y0z1] Account not found: FakeAccount
[StreamDeck] ERROR | [REQ:b2c3d4] Failed to submit market order — InvalidOperationException: ...
```

### Stream Deck Plugin
```
[timestamp] [NTDeck][LEVEL][REQ:id] message
```
Exemple :
```
2025-06-15T14:30:00.123Z [NTDeck][INFO][REQ:a1b2c3d4] Sending buyMarket
2025-06-15T14:30:00.225Z [NTDeck][INFO][REQ:a1b2c3d4] buyMarket succeeded: Buy 2 ES Market submitted
2025-06-15T14:30:01.000Z [NTDeck][WARN][REQ:e5f6g7h8] buyMarket failed: INVALID_QUANTITY — Quantity must be >= 1
```

## Scénarios de diagnostic

### "Mon action ne fait rien"
1. Vérifier le log plugin : est-ce que le message est envoyé ?
2. Vérifier le log bridge : est-ce que le message y arrive ? Est-il validé ?
3. Vérifier le log NT8 : est-ce que l'action arrive au dispatcher ?
4. Chercher le requestId dans les 3 logs

### "Mon ordre est rejeté"
1. Chercher le requestId dans le log NT8
2. Vérifier le code erreur retourné (ACCOUNT_NOT_FOUND, NO_POSITION, etc.)
3. Vérifier les préconditions (position existe, stop existe, etc.)

### "La connexion est instable"
1. Log bridge : chercher les patterns "connected" / "disconnected"
2. Log NT8 : "Connected to bridge" / "Disconnected from bridge"
3. Vérifier qu'aucun firewall ne bloque les ports 8218/8219 sur localhost
