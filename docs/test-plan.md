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

## 5bis. Tests macro Auto TP/SL

Voir `docs/macro-tp-sl.md`. Tous sur Sim101, tick ES = 0.25.

### T-46 : Armement refusé sans distance
- **Préconditions** : Touche Auto TP/SL posée, TP = 0 et SL = 0
- **Action** : Presser la touche
- **Résultat** : Reste `OFF`, la touche affiche `REGLER` si on l'arme autrement.
  Journal : `AutoTPSL | Armement refusé — aucune distance réglée sur la touche`

### T-47 : Les deux jambes, Long
- **Préconditions** : Armée, TP = 40, SL = 20. Flat
- **Action** : Buy Market 1 ES à 5420.00
- **Résultat** : Stop @ 5415.00 et Limit @ 5430.00, **même OCO**, quantité 1.
  Touche : `POSE`

### T-48 : Les deux jambes, Short
- **Préconditions** : Armée, TP = 40, SL = 20. Flat
- **Action** : Sell Market 1 ES à 5420.00
- **Résultat** : Stop @ 5425.00 et Limit @ 5410.00. Les niveaux sont **inversés** par rapport au T-47

### T-49a : TP seul
- **Préconditions** : Armée, TP = 40, SL = 0
- **Action** : Buy Market 1 ES
- **Résultat** : Limit seule, **aucun stop créé**. Touche : `TP40 SL--`

### T-49b : SL seul
- **Préconditions** : Armée, TP = 0, SL = 20
- **Action** : Buy Market 1 ES
- **Résultat** : Stop seul, **aucune limite créée**

### T-49c : OCO — l'un annule l'autre
- **Préconditions** : Position protégée par le T-47
- **Action** : Laisser le take profit s'exécuter (ou le déclencher à la main)
- **Résultat** : Le stop est **annulé par NinjaTrader**, la position reste à plat.
  Aucun ordre en attente ne subsiste

### T-49d : Renfort de position
- **Préconditions** : Long 1 ES @ 5420 protégé, TP = 40, SL = 20
- **Action** : Buy Market 1 ES à 5424 (prix moyen → 5422)
- **Résultat** : Stop **déplacé** à 5417.00 et Limit à 5432.00, **quantité passée à 2**.
  Aucune seconde paire créée. Journal : `Prix moyen modifié — protections recalculées`

### T-49e : Stop déjà en place (ATM ou manuel)
- **Préconditions** : Armée TP = 40 / SL = 20, position ouverte avec un stop posé à la main
- **Action** : Attendre la pose
- **Résultat** : Le stop manuel est **intact** ; seule la limite est créée.
  Réponse : `stopOutcome: "kept:foreign"`

### T-49f : Armement sur une position déjà en gain
- **Préconditions** : Long 1 ES @ 5420, marché à 5440. Armer avec TP = 40
- **Action** : Attendre la pose
- **Résultat** : Le take profit (5430) est **refusé**, pas exécuté.
  Journal : `Take profit NOT placed … already past the market`

### T-49g : Cohabitation avec l'Auto BE
- **Préconditions** : Auto TP/SL (SL = 20) **et** Auto BE (seuil 8) armées
- **Action** : Buy Market, laisser le gain atteindre 8 ticks
- **Résultat** : **Un seul** stop tout du long : celui posé par l'Auto TP/SL, déplacé au point mort
  par l'Auto BE

### T-49h : Distance décimale
- **Préconditions** : Saisir 20,5 dans le champ Stop Loss
- **Résultat** : L'éditeur arrondit (`step: 1`). Si la valeur atteint quand même le bridge,
  refus `INVALID_PAYLOAD` — jamais une protection silencieusement désactivée

### T-49i : Distance modifiée en séance
- **Préconditions** : Long 1 ES @ 5420 protégé, SL = 20
- **Action** : Passer le Stop Loss à 40 dans l'éditeur, sans fermer la position
- **Résultat** : Le stop est **déplacé** à 5410.00 sur la position en cours.
  Journal : `Distances modifiées — protections repositionnées sur la position en cours`

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

## 7bis. Tests des garde-fous d'exécution

### T-60 : Ordre limite sans flux de prix
- **Préconditions** : Instrument suivi sans souscription de données (aucun chart ouvert)
- **Action** : Buy Limit puis Sell Limit
- **Résultat** : Refusé avec `NO_MARKET_DATA`, **aucun ordre transmis**.
  Régression visée : la limite partait à `0 + offset × tick`, et une limite vendeuse
  sous le marché s'exécutait instantanément au marché

### T-61 : Rejet NinjaTrader remonté
- **Préconditions** : Provoquer un rejet (quantité au-delà de la marge, ou marché fermé)
- **Action** : Buy Market
- **Résultat** : Log `ORDER REJECTED by NinjaTrader: … — <raison>`, `showAlert` sur les
  touches, `REJECTED` affiché 5 s **sans griser** la touche (grisé = bloqué, jamais rejeté)

### T-62 : Break-even sur trade en perte
- **Préconditions** : Position Long en perte, stop actif
- **Action** : BE
- **Résultat** : `INVALID_STOP_PRICE` avec un message indiquant que le trade n'est pas en
  profit — et non un `ORDER_REJECTED` opaque

### T-63 : Move Stop sur position scalée
- **Préconditions** : Position avec 2 stops actifs à des prix différents
- **Action** : Stop +1
- **Résultat** : **Les deux** stops décalés de 1 tick, `stopsModified: 2`.
  Régression visée : un seul stop arbitraire était déplacé, laissant une partie de la
  position protégée à l'ancien niveau

### T-64 : Reverse avec protections actives
- **Préconditions** : Long 2 avec stop et target actifs
- **Action** : Reverse
- **Résultat** : Les ordres de protection sont annulés **avant** le retournement
  (`ordersCancelled` > 0), puis Short 2. Aucun ordre orphelin ne subsiste

### T-65 : Cancel Orders ne ferme pas la position
- **Préconditions** : Position ouverte + 1 limite d'entrée en attente
- **Action** : Cancel Orders
- **Résultat** : La limite est annulée, **la position reste ouverte**.
  À comparer avec Close All, qui ferme tout

### T-66 : Aucune confirmation d'ordre perdue sous charge
- **Préconditions** : Add-on connecté, publication d'état toutes les 500 ms
- **Action** : 30 pressions Buy/Sell Market espacées de ~200 ms
- **Résultat** : 30 réponses reçues, aucun `TIMEOUT`, aucune déconnexion NT8.
  Régression visée : un envoi concurrent abortait la socket
  (`InvalidOperationException` → état `Aborted`), la réponse était perdue et le trader
  re-pressait la touche sur un ordre déjà exécuté

### T-67 : Position fantôme
- **Préconditions** : Position ouverte, puis rendre l'instrument non résoluble
- **Résultat** : La position disparaît du deck au lieu de rester figée avec un Close
  qui échouerait en `INSTRUMENT_NOT_FOUND`

## 8bis. Tests macro de sécurité verrouillable

Réglages utilisés : `maxTradesWhenLosing = 3`, `dailyLossLimit = 300`,
`lockDurationHours = 0.05` (3 min — minimum autorisé, pour ne pas attendre 6h).

### T-72 : Configuration puis armement
- **Préconditions** : Macro désarmée
- **Action** : Régler les 3 champs dans le Property Inspector, presser Safety Macro
- **Résultat** : Touche verte, compte à rebours affiché, `armed=true`, `locked=true`

### T-73 : Désarmement refusé pendant le verrou
- **Préconditions** : Macro armée, verrou en cours
- **Action** : Presser Safety Macro
- **Résultat** : `SAFETY_MACRO_LOCKED`, `showAlert` sur la touche, macro toujours armée

### T-74 : Reconfiguration refusée pendant le verrou
- **Préconditions** : Macro armée
- **Action** : Modifier `dailyLossLimit` dans le Property Inspector
- **Résultat** : `SAFETY_MACRO_LOCKED`, les limites verrouillées restent en vigueur

### T-75 : Limite de trades atteinte en perte
- **Préconditions** : Macro armée, 3 trades ouverts/fermés, PnL de session négatif
- **Action** : Buy Market
- **Résultat** : `SAFETY_TRADE_LIMIT_REACHED`, **aucun ordre transmis à NT8**,
  touches d'entrée affichent `MAX TRADES`

### T-76 : Limite de trades inerte si PnL positif
- **Préconditions** : Macro armée, 3 trades, PnL de session positif
- **Action** : Buy Market
- **Résultat** : Ordre soumis normalement

### T-77 : Perte journalière atteinte
- **Préconditions** : Macro armée, PnL de session à -300 (réalisé et/ou latent)
- **Action** : Buy Market / Sell Limit / Reverse
- **Résultat** : `SAFETY_DAILY_LOSS_REACHED` pour les 3, touches affichent `LOSS LIMIT`

### T-78 : Les sorties restent disponibles
- **Préconditions** : Macro armée et bloquante, position ouverte
- **Action** : Close, Cancel, BE, Stop ±, Target ±
- **Résultat** : Toutes fonctionnent — aucune erreur `SAFETY_*`

### T-79 : Redémarrage du bridge ne déverrouille pas
- **Préconditions** : Macro armée, verrou en cours
- **Action** : Tuer `StreamDeckBridge.exe`, le relancer
- **Résultat** : Au démarrage, log `Safety macro is ARMED and locked for another …`.
  `armed`, `locked`, `tradeCount`, limites et référence PnL sont conservés ;
  le désarmement reste refusé

### T-79b : Expiration du verrou
- **Préconditions** : Macro armée, attendre la fin du verrou
- **Résultat** : Log `Safety macro DISARMED (lock expired)`, touche grise `OFF`,
  désarmement et reconfiguration à nouveau autorisés, paramètres conservés

### T-79c : PnL du compte indisponible
- **Préconditions** : Add-on publiant `pnlAvailable = false`
- **Résultat** : Touche affiche `PNL?`, les règles PnL sont inertes (pas de blocage
  silencieux ni de faux blocage)

## 8ter. Tests de la copie de comptes

Les douze scénarios vivent dans **[macro-copieur.md](macro-copieur.md#vérifier)**, avec la macro
qu'ils valident plutôt que recopiés ici : ils portent chacun sur une règle du moteur de copie, et
séparer la règle de son test est le meilleur moyen de faire diverger les deux.

Deux d'entre eux ne doivent jamais être sautés :

- **n° 9** — position fermée à la main sur un suiveur : dérive détectée, entrées arrêtées, et
  **aucun ordre émis**. C'est la garantie centrale de cette macro ;
- **n° 11** — Guard bloque en cours de position : entrées arrêtées, **sorties toujours copiées**.
  Enfermer un suiveur dans une position est le résultat qu'aucune règle ne peut produire.

La moitié bridge est couverte par un harnais jetable (14 contrôles au 20/08/2026 : parsing des
multiplicateurs et plafonds, refus d'un suiveur égal au maître, bornes, retenue au changement de
compte maître et sa libération). Il vit dans le scratchpad, à reconstruire au besoin — même statut
que celui de `SafetyMacro`.

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
  - SafetyMacro : arm/disarm/configure sous verrou, évaluation des 2 règles,
    comptage des trades, bascule de jour, persistance et rechargement
  - BridgeClient (add-on) : envois concurrents sérialisés — à tester sur **net48**,
    net8 sérialise en interne et masque le bug
  - TradingEngine : refus sans flux de prix, BE du mauvais côté du marché,
    Move Stop/Target sur plusieurs ordres, Reverse annulant les protections
  - SimpleJson : NaN/Infinity ne doivent jamais produire du JSON invalide
  - StatusDisplayAction.getDisplayText : toutes les variantes

Tests d'intégration :
  - Bridge ↔ mock plugin (WebSocket client)
  - Bridge ↔ mock addon (WebSocket client)
  - Message round-trip : command → response

Tests end-to-end (manual + guidé) :
  - Tous les scénarios T-xx ci-dessus
  - Utiliser le paper trading Sim101 de NT8
```
