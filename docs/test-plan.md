# Plan de Test V1 — NinjaTrader × Stream Deck

## 1. Tests de démarrage et connexion

### T-01 : Démarrage normal des 3 composants
- **Préconditions** : NinjaTrader 8 installé, Bridge compilé, Plugin installé
- **Étapes** :
  1. Lancer NinjaTrader 8 (l'Add-On démarre automatiquement)
  2. Lancer le Bridge (`StreamDeckBridge.exe`)
  3. Ouvrir Stream Deck (le plugin se charge automatiquement)
- **Résultat attendu** :
  - Le Bridge affiche : "Plugin port: 8218, Add-On port: 8219"
  - NT8 Output Window : "[StreamDeck] INFO | Connected to bridge"
  - Plugin log : "Connected to bridge"
  - Les boutons Status affichent "NT 🟢"

### T-02 : Le Bridge démarre avant NT8
- **Étapes** :
  1. Lancer le Bridge en premier
  2. Lancer NT8 ensuite
- **Résultat attendu** : Le Bridge log "NinjaTrader Add-On connected" quand NT8 se connecte

### T-03 : NT8 n'est pas lancé
- **Étapes** :
  1. Lancer le Bridge et le plugin SD
  2. Ne pas lancer NT8
  3. Presser un bouton de trading
- **Résultat attendu** :
  - Le Bridge retourne `NT_DISCONNECTED`
  - Le plugin affiche un flash d'erreur
  - Le bouton Status Connection affiche "NT 🔴"

### T-04 : Reconnexion après perte NT8
- **Étapes** :
  1. Tout lancer normalement (état connecté)
  2. Fermer NT8
  3. Vérifier que les boutons reflètent la déconnexion
  4. Relancer NT8
- **Résultat attendu** : Reconnexion automatique en < 5s

### T-05 : Reconnexion plugin SD
- **Étapes** :
  1. Fermer l'application Stream Deck
  2. Rouvrir Stream Deck
- **Résultat attendu** : Le plugin se reconnecte au Bridge automatiquement

## 2. Tests de quantité

### T-10 : QTY +1
- **Préconditions** : Connecté, qty = 1
- **Action** : Presser le bouton QTY +1
- **Résultat** : qty = 2, le bouton Status QTY affiche "2"

### T-11 : QTY -1 (au minimum)
- **Préconditions** : qty = 1
- **Action** : Presser QTY -1
- **Résultat** : qty reste à 1 (min = 1)

### T-12 : QTY Reset
- **Préconditions** : qty = 7
- **Action** : Presser QTY Reset
- **Résultat** : qty revient à defaultQuantity (1)

### T-13 : QTY Preset
- **Action** : Presser bouton "QTY 5"
- **Résultat** : qty = 5

## 3. Tests ordres d'entrée

### T-20 : Buy Market
- **Préconditions** : Connecté, Sim101, ES, qty = 1, flat
- **Action** : Presser Buy Market
- **Résultat** :
  - NT8 soumet un ordre Market Buy 1 ES
  - Le plugin reçoit une réponse success
  - Le bouton Status Position affiche "▲ 1"

### T-21 : Sell Market
- Même test avec Sell Market, résultat : "▼ 1"

### T-22 : Buy Market — Compte introuvable
- **Préconditions** : Settings avec `account: "FakeAccount"`
- **Action** : Presser Buy Market
- **Résultat** : Erreur `ACCOUNT_NOT_FOUND`, pas d'ordre soumis

### T-23 : Buy Market — Instrument introuvable
- **Préconditions** : Settings avec `instrument: "FAKE 01-99"`
- **Action** : Presser Buy Market
- **Résultat** : Erreur `INSTRUMENT_NOT_FOUND`

### T-24 : Buy Limit (offset -2)
- **Préconditions** : Connecté, flat, ES dernier prix = 5425.00
- **Action** : Presser Buy Limit (offsetTicks = -2)
- **Résultat** : Ordre Limit Buy à 5424.50

### T-25 : Sell Limit (offset +2)
- **Résultat** : Ordre Limit Sell à 5425.50

## 4. Tests gestion de position

### T-30 : Flatten avec position
- **Préconditions** : Position Long 2 ES
- **Action** : Presser Flatten
- **Résultat** : Position aplatie, Status = "FLAT"

### T-31 : Flatten sans position
- **Préconditions** : Flat
- **Action** : Presser Flatten
- **Résultat** : Succès (opération idempotente — pas d'erreur)

### T-32 : Cancel Pending Orders
- **Préconditions** : 3 ordres limit en attente
- **Action** : Presser Cancel Orders
- **Résultat** : Les 3 ordres sont annulés, message "Cancelled 3 pending orders"

### T-33 : Reverse avec position Long
- **Préconditions** : Long 2 ES
- **Action** : Presser Reverse
- **Résultat** : Sell Market 4 (close 2 + open 2 short), position = Short 2

### T-34 : Reverse sans position
- **Action** : Presser Reverse
- **Résultat** : Erreur `NO_POSITION`

## 5. Tests Break-Even

### T-40 : BE simple avec position
- **Préconditions** : Long 1 ES @ 5420.00, Stop @ 5415.00
- **Action** : Presser BE (offset = 0)
- **Résultat** : Stop déplacé à 5420.00

### T-41 : BE +2 avec position
- **Préconditions** : Long 1 ES @ 5420.00, Stop @ 5415.00
- **Action** : Presser BE+2 (offset = 2)
- **Résultat** : Stop déplacé à 5420.50 (5420 + 2 × 0.25)

### T-42 : BE sans position
- **Préconditions** : Flat
- **Action** : Presser BE
- **Résultat** : Erreur `NO_POSITION`

### T-43 : BE sans stop existant
- **Préconditions** : Long 1 ES, aucun stop order
- **Action** : Presser BE
- **Résultat** : Erreur `NO_STOP_ORDER`

### T-44 : BE Short
- **Préconditions** : Short 1 ES @ 5430.00, Stop @ 5435.00
- **Action** : Presser BE (offset = 0)
- **Résultat** : Stop déplacé à 5430.00

### T-45 : BE avec position partiellement réduite
- **Préconditions** : Long 3 ES @ 5420, réduit à 1 (avg price peut changer)
- **Action** : Presser BE
- **Résultat** : Stop déplacé au AveragePrice actuel de NT8

## 6. Tests Move Stop / Target

### T-50 : Stop +1 tick (Long)
- **Préconditions** : Long, Stop @ 5415.00
- **Action** : MoveStop delta = +1
- **Résultat** : Stop → 5415.25 (éloigné du prix = plus de marge)

### T-51 : Stop -1 tick (Long)
- **Action** : MoveStop delta = -1
- **Résultat** : Stop → 5414.75 (rapproché du prix = plus serré)

### T-52 : Move Stop sans position
- **Résultat** : Erreur `NO_POSITION`

### T-53 : Move Stop sans stop order
- **Résultat** : Erreur `NO_STOP_ORDER`

### T-54 : Move Target +1
- **Préconditions** : Long, Target @ 5430.00
- **Action** : MoveTarget delta = +1
- **Résultat** : Target → 5430.25

## 7. Tests d'instruments

### T-60 : Changement d'instrument
- **Action** : Presser bouton "NQ"
- **Résultat** : L'instrument actif passe à NQ, le bouton Status Instrument affiche "NQ"

### T-61 : Ordre après changement d'instrument
- **Préconditions** : Instrument changé à NQ
- **Action** : Buy Market
- **Résultat** : L'ordre est soumis sur NQ, pas ES

## 8. Tests Safe Mode

### T-70 : Ordre sur compte réel — Safe Mode ON
- **Préconditions** : `allowLiveAccounts = false`, account = "My Real Account"
- **Action** : Buy Market
- **Résultat** : Erreur `LIVE_ACCOUNT_BLOCKED`

### T-71 : Ordre sur Sim101 — Safe Mode ON
- **Préconditions** : `allowLiveAccounts = false`, account = "Sim101"
- **Résultat** : Ordre soumis normalement

## 9. Tests de robustesse

### T-80 : Pressions rapides répétées
- **Action** : Presser Buy Market 5 fois rapidement
- **Résultat** : 5 ordres distincts soumis, 5 requestIds différents, pas de duplicate

### T-81 : RequestId dupliqué
- **Action** : Envoyer manuellement un message avec le même requestId 2 fois
- **Résultat** : Le second est rejeté avec `DUPLICATE_REQUEST`

### T-82 : JSON invalide
- **Action** : Envoyer du texte non-JSON au Bridge
- **Résultat** : Log warning, pas de crash

### T-83 : Payload incomplet
- **Action** : Envoyer une commande `buyMarket` sans `quantity`
- **Résultat** : La quantité par défaut du state est utilisée (enrichissement par le Bridge)

## 10. Tests d'état / feedback visuel

### T-90 : Status Account
- **Résultat** : Affiche "Sim101"

### T-91 : Status Position — Long
- **Résultat** : Affiche "▲ 2 @ 5420"

### T-92 : Status P&L
- **Résultat** : Affiche "+$125" ou "-$50"

### T-93 : Status Connection — NT déconnecté
- **Résultat** : Affiche "NT 🔴"

## Base pour tests automatiques

### Infrastructure recommandée

```
Tests unitaires (xUnit / Jest) :
  - MessageValidator : toutes les combinaisons valide/invalide
  - StateManager : qty operations, bounds checking
  - DuplicateGuard : window expiry, detection
  - MessageRouter : routing logic, local actions
  - StatusDisplayAction.getDisplayText : toutes les variantes

Tests d'intégration :
  - Bridge ↔ mock plugin (WebSocket client)
  - Bridge ↔ mock addon (WebSocket client)
  - Message round-trip : command → response

Tests end-to-end (manual + guidé) :
  - Tous les scénarios T-xx ci-dessus
  - Utiliser le paper trading Sim101 de NT8
```
