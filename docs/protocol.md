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

#### `attachBracket` — pose le take profit et le stop loss
Calcule les deux niveaux depuis le **prix moyen** de la position ouverte, dans son sens, et les
soumet **liés en OCO** : l'un exécuté annule l'autre. Sans ce lien, un take profit exécuté
laisserait le stop actif sur une position à plat — et un stop sur une position à plat est une
*entrée* dès qu'il se déclenche.

Émise uniquement par l'automatisme **Auto TP/SL** de l'hôte (`host.autotpsl`), jamais par un appui
de touche. Elle part **après** l'exécution de l'entrée, pas avec elle : `Account.Submit` rend la
main avant le fill, un prix lu à cet instant serait une supposition. Rejouer la même commande après
un renfort suffit à faire suivre les deux jambes au nouveau prix moyen.

```json
{
  "type": "command",
  "action": "attachBracket",
  "payload": {
    "account": "Sim101",
    "instrument": "ES 06-25",
    "takeProfitTicks": 40,
    "stopLossTicks": 20
  }
}
```

| Champ | Plage | Description |
|-------|-------|-------------|
| `takeProfitTicks` | 0–10000, entier | Distance au-dessus du prix moyen en long, en dessous en short. `0` = **aucun take profit** |
| `stopLossTicks` | 0–10000, entier | Distance en dessous du prix moyen en long, au-dessus en short. `0` = **aucun stop loss** |

Une décimale est refusée en `INVALID_PAYLOAD` par le bridge, et volontairement : l'add-on la lirait
comme *absente*, donc comme `0`, donc comme « pas de protection ». Une protection désactivée en
silence par une virgule est le pire mode de défaillance de cette macro.

`0` sur les deux champs répond en succès sans rien envoyer. `0` ne veut jamais dire « annuler ce
qui est déjà posé » : la macro cesse de gérer cette jambe, elle ne retire pas une protection en
cours.

**Ce que la commande ne fait pas** :
- elle n'ajoute **jamais** une seconde protection du même côté. Un stop déjà en place — stratégie
  ATM, stop manuel, break-even — laisse la jambe à `kept:foreign` : la position est déjà couverte,
  et un second stop sortirait du double de la taille puis ouvrirait le trade inverse ;
- elle ne pose **jamais** un niveau déjà franchi par le marché (`refused:pastMarket`). L'ordre
  serait immédiatement exécutable : il ne protégerait pas le trade, il le clôturerait sur-le-champ.

Elle **annule** en revanche ses propres jambes restées d'une position de sens opposé, avant de
poser les nouvelles : ce sont les seuls ordres qu'elle puisse produire capables d'ouvrir un trade.

Réponse :
```json
{
  "result": {
    "success": true,
    "avgPrice": 5420.00, "quantity": 2, "direction": "Long",
    "stopPrice": 5415.00, "targetPrice": 5430.00,
    "stopOutcome": "created", "targetOutcome": "created",
    "legsPlaced": 2, "oco": "8f3c…"
  }
}
```

| Issue d'une jambe | Signification |
|-------------------|---------------|
| `created` | Ordre soumis |
| `modified` | Jambe déjà posée par cette macro, repositionnée et redimensionnée (renfort) |
| `kept:foreign` | Une protection que la macro ne possède pas couvre déjà ce côté — rien envoyé |
| `refused:pastMarket` | Le niveau est déjà dépassé par le marché — rien envoyé |
| `disabled` | La jambe vaut `0` dans les réglages |

Refusée en `INVALID_STOP_PRICE` quand **aucune** jambe n'a pu partir et que le marché en est la
cause. Une jambe laissée à une protection préexistante n'est pas un échec : la position est
couverte, ce qui était tout l'objet de la commande.

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
il n'existe volontairement aucun paramètre de contournement. Un drapeau `force` a existé
jusqu'en 0.4.0, activé par un « mode développement » sur la touche ; il est retiré depuis
0.5.0 et tout champ supplémentaire du payload est ignoré. Le verrou ne se lève qu'à son
échéance.
```json
{
  "type": "command",
  "action": "disarmSafety",
  "payload": {}
}
```

#### `toggleSafety`
Arme si désarmée, tente de désarmer si armée. C'est l'action derrière la touche
Stream Deck « Safety Macro ». Même règle que `disarmSafety` : aucun contournement du verrou.
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
    "lockDurationHours": 6,
    "antiTiltEnabled": false,
    "tiltAveragingAllowed": true,
    "tiltMaxContracts": 0
  }
}
```

| Champ | Plage | Description |
|-------|-------|-------------|
| `maxTradesWhenLosing` | 0–999 | Trades max une fois le PnL de session négatif. `0` = règle désactivée |
| `maxContracts` | 0–1000 | Contrats que le compte peut détenir. Une entrée qui porterait la position au-delà est **refusée** (`SAFETY_MAX_CONTRACTS`). `0` = règle désactivée, et c'est le défaut |
| `dailyLossLimit` | 0–1 000 000 | Perte de session max (nombre positif). `0` = règle désactivée |
| `lockDurationHours` | 0.05–24 | Durée du verrou après armement. Défaut `6` |
| `sessionStartEnabled` | booléen | Refuse toute ouverture avant `sessionStartTime`. Défaut `false` |
| `sessionStartTime` | `HH:mm` | Heure **locale** du poste avant laquelle les entrées sont refusées. Doit accompagner l'allumage : allumer sans heure est refusé (`SAFETY_START_TIME_MISSING`), une valeur illisible aussi (`SAFETY_INVALID_START_TIME`). Normalisée en `HH:mm` — `9:45` est accepté et enregistré `09:45` |
| `antiTiltEnabled` | booléen | Autorise la friction Anti-Tilt. Défaut `false` |
| `tiltAveragingAllowed` | booléen | `false` met sous friction tout renfort d'une position perdante. Défaut `true` |
| `tiltAdvanced` | booléen | Active les deux durées ci-dessous. À `false`, elles sont ignorées et les valeurs par défaut s'appliquent — sans effacer ce qui avait été saisi |
| `tiltHoldSeconds` | 15–30 | Maintien exigé sur une entrée ralentie. Défaut `20`. Ignoré si `tiltAdvanced` est `false` |
| `tiltEpisodeMinutes` | 1–60 | Durée d'un épisode. Défaut `15`. Ignoré si `tiltAdvanced` est `false` |

**L'Anti-Tilt ne refuse jamais un ordre et ne touche jamais au verrou.** Il signale seulement que
les entrées doivent être maintenues ; c'est l'hôte qui applique cette friction. Un épisode ne peut
donc pas verrouiller le deck — c'est la propriété qui le distingue des limites ci-dessus.

#### Seuils Anti-Tilt dérivés — non configurables

Les autres seuils ne sont **pas** exposés : une protection contre ses propres impulsions que l'on
calibre soi-même se fait desserrer au moment précis où elle sert. Ils viennent des conventions
usuelles de gestion du risque, et les deux qui ont besoin d'une échelle sont dérivés des limites
que le trader règle déjà — ce qui les adapte à un compte de 25 k comme à un compte bien plus gros.

| Seuil | Valeur | Fondement |
|-------|--------|-----------|
| Escalade de taille | **+50 %** après un trade perdant | Principe anti-martingale : ne jamais augmenter la taille pour récupérer une perte. C'est le réflexe décrit par le *break-even effect* (Thaler & Johnson, 1990) |
| Restitution de gain | **50 % de `dailyLossLimit`** | Les règles de *give-back* sont courantes chez les sociétés de prop trading. Exprimée en fraction de la perte déjà acceptée, elle s'échelonne avec le compte. `0` si `dailyLossLimit` vaut 0 : sans échelle, la règle reste inerte |
| Pertes consécutives | **`maxTradesWhenLosing / 3`**, minimum 3 (défaut 5 si le budget est désactivé) | La règle de bureau classique est « stop après 3 » ; à une cadence de scalping elle se déclenche sur la variance ordinaire, d'où l'indexation sur le budget de trades |
| Durée d'épisode | **15 min** | Temporisation assez longue pour casser la boucle, assez courte pour ne jamais condamner une séance — c'est ce qui la distingue du verrou de Guard. *Ajustable via `tiltAdvanced`* |
| Durée de maintien | **20 s** | Fixée par le cahier des charges du trader : 15 à 30 s, « pas moins ». *Ajustable via `tiltAdvanced`* |

Seules les deux **durées** sont ouvertes aux réglages avancés : ce sont des questions de confort.
Les trois **seuils** qui décident réellement d'un déclenchement (escalade, restitution, série de
pertes) restent inaccessibles, y compris en avancé — les rendre modifiables reviendrait à laisser
desserrer la protection au moment précis où elle sert.

Les valeurs effectivement retenues sont journalisées à chaque `configureSafety`, sous
`— derived: escalation=…, giveBack=…, lossStreak=…` : c'est le seul endroit où les relire.

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

#### Blocage avant une heure

Règle d'horloge, et la seule de Guard qui ne regarde rien de ce qui s'est passé dans la séance :
elle refuse toute **ouverture** de position avant une heure fixée à l'avance, et se retire d'
elle-même à l'heure dite. Elle vise l'heure où rien n'est encore décidé — l'ouverture, où l'on
prend une position par impatience plutôt que par plan.

- **Gardée par l'armement.** Comme les autres règles de séance, elle n'agit que si Guard est armé.
- **Entrées seulement.** `buyMarket`, `sellMarket`, `buyLimit`, `sellLimit`, `reverse`. Clôturer et
  réduire restent toujours possibles — enfermer le trader dans une position est le seul résultat
  qu'aucune règle de ce protocole ne peut produire.
- **Elle atteint aussi la souris.** Le refus passe dans `setGuardPolicy`
  (`blocked: true, reason: "beforeSessionStart"`), donc `GuardEnforcer` annule dans NinjaTrader un
  ordre saisi au SuperDOM avant l'heure. Une règle qui ne tiendrait que sur le boîtier serait une
  décoration.
- **Heure LOCALE du poste**, pas une heure de bourse : c'est celle que le trader lit sur son
  horloge, et la seule qu'il puisse vérifier d'un coup d'œil pendant que la touche est rouge.
- **Elle ne referme pas la séance le soir.** La comparaison porte sur l'heure du jour, pas sur le
  jour de bourse : à 20:00 un réglage `09:45` laisse passer. C'est assumé — une règle sur le début
  de la journée n'a pas à décider de sa fin. Une session overnight qui ouvre à 18:00 n'est donc pas
  contrainte par un réglage du matin.

### Tendance

#### Ce que le bridge retient d'une session à l'autre

`%APPDATA%\StreamDeckTrader\session.json` conserve **l'instrument et le compte** sélectionnés :

```json
{ "instrument": "MNQ", "account": "APEX-346280-35" }
```

À la connexion de l'add-on, le bridge repousse les deux — `setAccount` **d'abord**, puis
`setInstrument`. L'ordre compte : sans compte, l'add-on suit le premier que NinjaTrader liste, et
deux règles de sécurité suivent ce défaut en silence — `GuardEnforcer` surveille le compte suivi, et
le **maître du copieur est le compte sélectionné**. Le 21/08/2026 un redémarrage a ainsi pointé
l'enforcement sur un compte que personne ne tradait et interverti le maître avec son suiveur,
pendant que le boîtier affichait la macro armée.

Si aucun compte n'a jamais été choisi, le bridge journalise un avertissement et laisse le défaut de
l'add-on : rien à restaurer, mais tout ce qui suit repose alors sur un compte arbitraire.

#### `configureTrend`
Règle la macro Tendance. **Chemin double** : le bridge valide et accuse réception, puis transmet à
l'add-on — seul à pouvoir agir, puisque c'est lui qui détient les barres. Même mécanique que
`setInstrument` / `setAccount`.

Tous les champs sont facultatifs, mais il en faut **au moins un**. Un champ absent laisse la valeur
courante en place : l'hôte rejoue toute sa configuration à chaque édition du layout et à chaque
reconnexion, et une omission ne doit jamais valoir remise à zéro.

Changer `referenceMinutes` ou `higherMinutes` est **structurel** : l'add-on recharge ses séries,
donc repasse par `available: false` pendant quelques secondes.

```json
{
  "type": "command",
  "action": "configureTrend",
  "payload": {
    "referenceMinutes": 1,
    "higherEnabled": true,
    "higherMinutes": 5,
    "thresholdAtr": 1.0
  }
}
```

| Champ | Plage | Description |
|-------|-------|-------------|
| `referenceMinutes` | 1–240, entier | Unité principale. Défaut `1` |
| `higherEnabled` | booléen | Exiger l'accord d'une unité supérieure. Défaut `true` |
| `higherMinutes` | 1–1440, entier | Doit être **strictement supérieur** à `referenceMinutes`, sinon `INVALID_PAYLOAD` |
| `thresholdAtr` | > 0 et ≤ 10 | Amplitude minimale d'une vague, en multiples d'ATR. Défaut `1.0` |
| `blockingAllowed` | booléen | Autorise l'armement. Défaut `false`. **Lu par le bridge**, pas par l'add-on |

`blockingAllowed` est le seul champ que le bridge consomme lui-même — c'est lui qui refuse. Le
passer à `false` **désarme** la macro : masquer une règle sans la neutraliser laisserait une
protection active dont plus rien à l'écran ne dirait qu'elle l'est.

#### `toggleTrend`
Arme la macro, ou la désarme. Aucun payload. Déclenché par un **maintien** de la touche côté hôte ;
un appui bref ne l'envoie pas.

```json
{ "type": "command", "action": "toggleTrend", "payload": {} }
```

Réponse : `{ "success": true, "trendArmed": true }`.

Refusé par `TREND_BLOCKING_DISABLED` tant que `blockingAllowed` vaut `false` — sinon un maintien
armerait une protection que le trader n'a jamais demandée, et il la découvrirait au premier ordre
refusé.

Contrairement à la macro de sécurité, **il n'y a pas de verrou** : la Tendance se désarme aussi
librement qu'elle s'arme. Ce n'est pas une limite de risque adossée à un fait comptable mais une
aide à la discipline, et le trader doit pouvoir la relâcher quand sa lecture du marché change.
L'armement n'est pas persisté (voir [macro-trend.md](macro-trend.md)).

### Copie de comptes

Portée par la touche **Compte**, pas par une action à elle : le compte maître **est** le compte
sélectionné. Détail complet dans [macro-copieur.md](macro-copieur.md).

#### `configureCopier`
```json
{
  "type": "command",
  "action": "configureCopier",
  "payload": { "enabled": true, "followers": "Sim102|1|0\nSim103|2|5" }
}
```

| Champ | Description |
|-------|-------------|
| `enabled` | Le réglage « Copier les positions » de la touche Compte |
| `followers` | **Une chaîne**, une ligne par compte lié : `nom\|multiplicateur\|plafond`. L'hôte n'émet aujourd'hui que `nom\|1\|0` |

Le compte maître **n'est pas transmis** : le bridge le connaît déjà. Un second endroit où le
déclarer aurait été un second endroit où il peut diverger.

`followers` voyage en texte et non en tableau parce que `sanitizeSettings` côté Bitlearn n'accepte
que `string`, `boolean` et `number` dans les réglages d'une touche : un tableau y serait écarté en
silence, et la sélection du trader disparaîtrait sans message.

`followers` est la liste **déjà résolue** par l'hôte : le groupe configuré moins le compte
sélectionné. C'est là que se fait la bascule des rôles, et c'est pourquoi l'hôte renvoie cette
commande à chaque changement de compte, pas seulement à chaque édition du layout.

#### `copierPanic`
```json
{ "type": "command", "action": "copierPanic", "payload": {} }
```

Désactive la copie puis liquide chaque suiveur résolu. **Aucune touche ne produit cette action** —
elle existe dans le protocole, le geste qui la déclenchera reste à choisir.

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
    "trend": {
      "available": true,
      "direction": "up",
      "reference": "up",
      "higher": "up",
      "referenceMinutes": 1,
      "higherMinutes": 5,
      "staleSeconds": 12
    },
    "quantity": 5
  }
}
```

Le bloc `trend` est calculé par l'add-on, seul à disposer de barres — le bridge le transporte sans
l'interpréter. **Aucune donnée de marché ne traverse le pont** : on transmet un verdict, jamais une
barre.

| Champ | Description |
|-------|-------------|
| `available` | `false` tant qu'une série manque, charge, ou est périmée. Signifie **« on ne sait pas »**, et ne refuse donc rien — même posture que `pnlAvailable` |
| `direction` | Verdict combiné : `"up"`, `"down"` ou `"neutral"`. Accord **strict** des deux unités quand `higherMinutes > 0`, sinon `reference` |
| `reference` | Unité principale seule. Affichage et diagnostic |
| `higher` | Unité supérieure seule. `""` quand la confirmation est coupée |
| `referenceMinutes` / `higherMinutes` | Unités en vigueur. `higherMinutes: 0` = confirmation coupée |
| `staleSeconds` | Secondes depuis la dernière barre clôturée de la série la plus lente |
| `blockingAllowed` | Le blocage est autorisé par les réglages de la touche. **Ajouté par le bridge** |
| `armed` | La macro refuse actuellement les entrées à contre-sens. **Ajouté par le bridge** |

Les deux derniers champs n'existent pas dans ce que publie l'add-on : le bridge les estampille sur
le snapshot qu'il diffuse. Une publication de NinjaTrader ne peut donc pas les écraser.

`reference` et `higher` restent renseignés même quand `available` vaut `false` : c'est ce qui rend
un `NO DATA` lisible — on voit **quelle** série manque plutôt que de le deviner.

Le bloc est absent tant que l'add-on n'a pas initialisé son monitor ; le bridge conserve alors la
dernière valeur reçue plutôt que de la remettre à zéro, sinon la touche clignoterait entre un sens
connu et `NO DATA` à chaque publication. Il est en revanche **effacé quand NinjaTrader se
déconnecte** : plus de barres, donc plus de tendance.

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
    "tradingDay": "2026-07-29",
    "sessionStartEnabled": true,
    "sessionStartTime": "09:45",
    "sessionStartInSeconds": 0,
    "tiltEnabled": true,
    "tiltActive": true,
    "tiltSecondsRemaining": 742,
    "tiltReason": "giveBack",
    "tiltScope": "all",
    "tiltHoldSeconds": 20
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
| `blockReason` | `""`, `"dailyLoss"`, `"tradeLimit"`, `"maxContracts"`, `"mandatoryPause"` ou `"beforeSessionStart"` |
| `sessionStartEnabled` | La règle « pas d'entrée avant l'heure » est active |
| `sessionStartTime` | Heure locale réglée, `HH:mm`. **Vide quand la règle est coupée** — ce qui distingue « armée » de « réglée mais éteinte » dans le journal |
| `sessionStartInSeconds` | Secondes avant l'ouverture des entrées. `0` si la règle est coupée ou l'heure passée |
| `maxContracts` | Plafond de contrats en vigueur. `0` = règle désactivée |
| `tiltEnabled` | La friction Anti-Tilt est autorisée |
| `tiltActive` | Les entrées doivent être maintenues avant de partir. **Jamais un refus** |
| `tiltSecondsRemaining` | Secondes restantes sur l'épisode. `0` pour les conditions contextuelles, qui n'ont pas de minuteur |
| `tiltReason` | `""`, `"sizeEscalation"`, `"giveBack"`, `"consecutiveLosses"`, `"averaging"` ou `"maxContracts"` |
| `tiltScope` | `"all"` (épisode : toutes les entrées) ou `"increaseOnly"` (condition contextuelle : seulement les ordres qui augmentent l'exposition) |
| `tiltHoldSeconds` | Durée de maintien exigée |

`tiltReason` reste renseigné même quand `tiltActive` vaut `false` : la détection tourne en
permanence, y compris macro désarmée ou Anti-Tilt éteint, ce qui fait du mode désactivé un mode
d'observation — le journal dit ce que les règles auraient fait.

Le bridge y ajoute aussi le bloc `copier`, dont il possède la moitié configuration :

```json
{
  "copier": {
    "enabled": true, "master": "Sim101", "masterResolved": true,
    "entriesBlocked": false,
    "followers": [
      { "name": "Sim102", "multiplier": 1, "maxContracts": 0,
        "resolved": true, "drifted": false, "overCap": false, "drift": 0, "lastError": "" }
    ],
    "copiedToday": 14
  }
}
```

| Champ | Description |
|-------|-------------|
| `enabled` | **Bridge.** Le réglage « Copier les positions » |
| `master` | **Bridge.** Le compte sélectionné ; il n'existe pas de réglage séparé |
| `masterResolved` | Add-on. Le nom correspond à un compte NinjaTrader actif |
| `entriesBlocked` | **Bridge.** Guard refuse les entrées : leurs copies s'arrêtent, **les sorties continuent** |
| `multiplier` / `maxContracts` | **Bridge.** Toujours `1` et `0` aujourd'hui : un compte lié reçoit exactement la quantité du maître. Le moteur sait dimensionner par compte, mais aucun réglage ne l'expose — l'hôte normalise donc avant d'envoyer |
| `resolved` | Add-on. Le compte existe et sa connexion est active **en ce moment** |
| `drifted` | Add-on. Le compte ne reçoit plus d'entrées — écart installé **ou** dépassement de plafond. **Rien n'est envoyé pour corriger** |
| `overCap` | Add-on. Précise le motif : `drifted` vient d'une position **au-dessus du plafond**, pas d'une divergence avec le maître. La touche affiche `PLAFOND` au lieu de `DERIVE` |
| `drift` | Add-on. Écart signé en contrats : `réel − attendu` pour une dérive, contrats **au-dessus du plafond** quand `overCap` est vrai — le maître pouvant lui aussi le dépasser, l'écart entre les deux ne dirait rien |
| `lastError` | Add-on. Dernier refus sur ce suiveur, vide sinon |

Même partage que `trend` : ce que l'add-on publie ne peut pas écraser la configuration, que le
bridge réestampille sur chaque instantané.

### `setCopierConfig` — bridge → add-on

Ce que l'add-on doit recopier, et où.

```json
{ "type": "command", "action": "setCopierConfig",
  "payload": { "enabled": true, "master": "Sim101", "entriesBlocked": false,
               "followers": [ { "name": "Sim102", "multiplier": 1, "maxContracts": 0 } ] } }
```

Envoyé à la connexion de l'add-on (forcé) puis **uniquement au changement** — même discipline que
`setGuardPolicy`, et pour la même raison : la boucle tourne à 5 Hz.

`entriesBlocked` est le report de la décision de Guard ; l'add-on ne la recalcule pas.

### `copierViolation` — add-on → bridge → hôte

Même forme que `guardViolation`. Émis sur un rejet de suiveur, un échec d'envoi, et à l'entrée en
dérive.

```json
{ "type": "event", "action": "copierViolation",
  "payload": { "follower": "Sim103", "reason": "drift",
               "instrument": "MNQ 09-26", "drift": -2 } }
```

`reason` vaut `"drift"`, `"maxContracts"`, `"rejected"` ou `"submitFailed"`.

`"maxContracts"` est émis à deux moments : quand une copie d'entrée est **refusée ou rabotée** parce
que le suiveur atteindrait son plafond, et quand un suiveur est constaté **au-dessus** de ce
plafond. Le plafond retenu est le plus serré des deux : celui du suiveur et celui de la macro de
sécurité.

### `setGuardPolicy` — bridge → add-on

Le bridge publie ce que la macro refuse actuellement, afin que l'add-on applique les mêmes règles
aux ordres qui **ne traversent jamais le bridge** : SuperDOM, Chart Trader, DOM.

```json
{ "type": "command", "action": "setGuardPolicy",
  "payload": { "blocked": true, "reason": "dailyLoss", "maxContracts": 3 } }
```

Envoyé à la connexion de l'add-on (forcé) puis **uniquement au changement** — la boucle de
diffusion tourne à 5 Hz, un envoi inconditionnel noierait l'add-on et son journal.

**Portée : le compte suivi ET tout le groupe de copie.** L'add-on inspecte les ordres du compte
suivi (`OrderMonitor`) et, quand la copie tourne, ceux du maître et de chaque suiveur résolu
(`CopyEngine`). Ce n'était pas le cas avant le 21/08/2026 : l'enforcement ne voyait qu'un compte,
et un compte lié restait joignable à la souris sans qu'aucune règle ne le regarde. Un compte
NinjaTrader **hors** du groupe reste hors de portée — c'est le périmètre que le deck gouverne.

Le plafond `maxContracts` s'applique aussi **avant** l'envoi d'une copie, contre la position que
cette copie produirait sur le suiveur. Une copie est volontairement exemptée de l'annulation de
`GuardEnforcer` (pour ne pas appliquer deux fois, et asymétriquement, la règle déjà passée sur le
maître) : le plafond l'atteint donc à la soumission, ou jamais.

### `guardViolation` — add-on → bridge → hôte

Émis quand un ordre passé directement dans NinjaTrader est refusé par l'add-on pendant un blocage.

```json
{ "type": "event", "action": "guardViolation",
  "payload": { "violation": "dailyLoss", "cancelled": true, "orderId": "…",
               "orderAction": "Buy", "orderType": "Limit", "quantity": 2,
               "name": "", "instrument": "MNQ 09-26" } }
```

| Champ | Description |
|-------|-------------|
| `violation` | `"dailyLoss"`, `"tradeLimit"`, `"maxContracts"` ou `"guardBlocked"` |
| `cancelled` | `false` signale l'ordre **vu mais non annulé** — presque toujours un ordre au marché exécuté avant que la plateforme ne le remonte |
| `name` | Nom de l'ordre. Les ordres du deck portent `"StreamDeck"` et ne sont jamais inspectés |

**Ce que l'add-on n'annule jamais** : un ordre qui *réduit* l'exposition. Un stop manuel sur un long
est un `Sell`, sur un short un `BuyToCover` — les deux passent intacts même quand toutes les entrées
sont refusées. Enfermer le trader dans une position est le seul résultat que ces règles ne peuvent
pas produire.

**Limite assumée** : un ordre au marché sur un contrat liquide s'exécute souvent avant d'être
signalé, et un ordre exécuté ne s'annule pas. Ce cas est détecté et remonté, pas empêché. Et rien
n'atteint quelqu'un qui désactive l'add-on ou trade depuis la plateforme de son courtier : le but
est de transformer une impulsion de deux clics en démontage délibéré de son propre garde-fou.

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
| `SAFETY_MAX_CONTRACTS` | L'ordre porterait la position au-delà du plafond de contrats. Seuls les ordres qui augmentent l'exposition sont refusés : réduire reste toujours possible. **Seule règle Guard qui s'applique même macro désarmée** — c'est une limite de risque permanente, pas une règle de séance |
| `SAFETY_BEFORE_SESSION_START` | L'heure de début de séance n'est pas atteinte — ouverture refusée. Seules les entrées : clôturer et réduire restent possibles. La règle se retire **seule** à l'heure dite |
| `SAFETY_INVALID_START_TIME` | `sessionStartTime` illisible. Attendu `HH:mm` local, `00:00`–`23:59`. **Refusé plutôt qu'ignoré** : une heure que personne ne sait lire donnerait une protection que le trader croit avoir armée |
| `SAFETY_START_TIME_MISSING` | `sessionStartEnabled: true` sans heure à faire respecter, ni dans la charge ni déjà enregistrée |
| `SAFETY_MACRO_LOCKED` | Désarmement / reconfiguration impossible avant la fin du verrou |
| `COOLDOWN_ACTIVE` | Cooldown actif après un trade perdant |
| `TREND_AGAINST` | La macro Tendance est armée et l'ordre irait à contre-sens. Seuls les ordres qui **créent** de l'exposition sont refusés : clôturer et réduire restent toujours possibles |
| `TREND_BLOCKING_DISABLED` | Armement refusé : le blocage n'est pas autorisé dans les réglages de la touche |
| `COPIER_MASTER_IS_FOLLOWER` | Un suiveur porte le nom du compte sélectionné : il se copierait vers lui-même, doublant chaque ordre sur un seul compte |
| `COPIER_TOO_MANY_FOLLOWERS` | Plus de huit suiveurs. Chacun multiplie l'exposition d'un appui |
| `CONTEXT_MISSING` | Contexte insuffisant pour résoudre l'action |
| `ORDER_REJECTED` | NT8 a rejeté l'ordre |
| `INTERNAL_ERROR` | Erreur interne inattendue |
