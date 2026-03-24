# Layouts Stream Deck V1 — Guide de configuration

## Page 1 : Exécution (page principale)

Configuration pour un Stream Deck standard (5×3 = 15 boutons).

```
┌───────────┬───────────┬───────────┬───────────┬───────────┐
│  BUY MKT  │ SELL MKT  │  BUY LMT  │ SELL LMT  │  FLATTEN  │
│   (vert)  │  (rouge)  │(vert dim) │(rouge dim)│ (orange)  │
│ buymarket │sellmarket │ buylimit  │ selllimit │  flatten  │
├───────────┼───────────┼───────────┼───────────┼───────────┤
│  CANCEL   │ REVERSE   │    BE     │   BE +2   │  QTY [n]  │
│  (jaune)  │ (violet)  │  (bleu)   │  (bleu)   │  (blanc)  │
│cancelordrs│  reverse  │breakeven  │breakeven  │  status   │
│           │           │ offset=0  │ offset=2  │ type=qty  │
├───────────┼───────────┼───────────┼───────────┼───────────┤
│  STOP -1  │  STOP +1  │  TGT -1   │  TGT +1   │  → INFO   │
│           │           │           │           │ (page 4)  │
│ movestop  │ movestop  │movetarget │movetarget │           │
│ delta=-1  │ delta=+1  │ delta=-1  │ delta=+1  │           │
└───────────┴───────────┴───────────┴───────────┴───────────┘
```

## Page 2 : Quantité

```
┌───────────┬───────────┬───────────┬───────────┬───────────┐
│   QTY 1   │   QTY 2   │   QTY 5   │  QTY 10   │  QTY 20   │
│ qtypreset │ qtypreset │ qtypreset │ qtypreset │ qtypreset │
│  qty=1    │  qty=2    │  qty=5    │  qty=10   │  qty=20   │
├───────────┼───────────┼───────────┼───────────┼───────────┤
│   QTY +1  │   QTY -1  │ QTY RESET │           │  → EXEC   │
│ qtyadjust │ qtyadjust │ qtyreset  │           │ (page 1)  │
│  delta=1  │ delta=-1  │           │           │           │
├───────────┼───────────┼───────────┼───────────┼───────────┤
│  QTY [n]  │           │           │           │  → INFO   │
│  status   │           │           │           │ (page 4)  │
│  type=qty │           │           │           │           │
└───────────┴───────────┴───────────┴───────────┴───────────┘
```

## Page 3 : Instruments

```
┌───────────┬───────────┬───────────┬───────────┬───────────┐
│    ES     │    NQ     │    YM     │    CL     │    GC     │
│instrument │instrument │instrument │instrument │instrument │
│"ES 06-25" │"NQ 06-25" │"YM 06-25" │"CL 07-25" │"GC 08-25" │
├───────────┼───────────┼───────────┼───────────┼───────────┤
│    6E     │    6B     │    ZB     │   RTY     │   MES     │
│instrument │instrument │instrument │instrument │instrument │
│"6E 06-25" │"6B 06-25" │"ZB 06-25" │"RTY 06-25"│"MES 06-25"│
├───────────┼───────────┼───────────┼───────────┼───────────┤
│ [INSTR]   │           │           │           │  → EXEC   │
│  status   │           │           │           │ (page 1)  │
│type=instr │           │           │           │           │
└───────────┴───────────┴───────────┴───────────┴───────────┘
```

> **Note** : Les noms d'instruments incluent le mois/année de l'échéance.
> Ajustez-les selon le contrat actif du moment.

## Page 4 : Statut / Infos

```
┌───────────┬───────────┬───────────┬───────────┬───────────┐
│  ACCOUNT  │  INSTR    │ POSITION  │   P&L     │   QTY     │
│  Sim101   │ ES 06-25  │  ▲ 2      │  +$125    │    5      │
│  status   │  status   │  status   │  status   │  status   │
│type=accnt │type=instr │type=pos   │ type=pnl  │ type=qty  │
├───────────┼───────────┼───────────┼───────────┼───────────┤
│  BRIDGE   │   NT8     │ SAFE MODE │           │  → EXEC   │
│    🟢     │    🟢     │    ON     │           │ (page 1)  │
│  status   │  status   │  status   │           │           │
│type=conn  │type=conn  │           │           │           │
├───────────┼───────────┼───────────┼───────────┼───────────┤
│  → QTY    │  → INSTR  │           │           │           │
│ (page 2)  │ (page 3)  │           │           │           │
└───────────┴───────────┴───────────┴───────────┴───────────┘
```

## Configuration des actions dans le Property Inspector

### Buy/Sell Market
Aucune configuration spécifique — utilise le compte, instrument et quantité globaux.

### Buy/Sell Limit
- **offsetTicks** : décalage en ticks depuis le dernier prix
  - Buy Limit : typiquement `-2` (2 ticks en dessous)
  - Sell Limit : typiquement `+2` (2 ticks au-dessus)

### Break-Even
- **offsetTicks** :
  - `0` → BE simple (stop au prix d'entrée)
  - `2` → BE + 2 ticks de profit
  - Configurable : n'importe quelle valeur positive

### Move Stop / Move Target
- **deltaTicks** :
  - `+1` → éloigne du prix (donne plus de marge)
  - `-1` → rapproche du prix (resserre)

### Qty Preset
- **quantity** : la valeur que ce bouton fixe (1, 2, 5, 10, 20...)

### Qty Adjust
- **delta** : `+1` ou `-1`

### Instrument Select
- **instrument** : nom complet NT8 (ex: `"ES 06-25"`)
- **displayLabel** : label court affiché (ex: `"ES"`)

### Status Display
- **statusType** : `account` | `instrument` | `position` | `pnl` | `quantity` | `connection`

## Code couleur recommandé

| Action | Couleur fond | Couleur texte |
|--------|-------------|---------------|
| Buy Market | #1B5E20 (vert foncé) | blanc |
| Sell Market | #B71C1C (rouge foncé) | blanc |
| Buy Limit | #2E7D32 (vert) border | blanc |
| Sell Limit | #C62828 (rouge) border | blanc |
| Flatten | #E65100 (orange) | blanc |
| Cancel Orders | #F9A825 (jaune) | noir |
| Reverse | #6A1B9A (violet) | blanc |
| Break-Even | #1565C0 (bleu) | blanc |
| Move Stop/Target | #37474F (gris foncé) | blanc |
| Qty Preset (actif) | #0277BD (bleu clair) | blanc |
| Qty Preset (inactif) | #263238 (gris) | gris clair |
| Instrument (actif) | #00695C (teal) | blanc |
| Instrument (inactif) | #263238 (gris) | gris clair |
| Status | #212121 (noir) | selon données |
| Connecté | - | #4CAF50 vert |
| Déconnecté | - | #F44336 rouge |
| Erreur flash | #D32F2F | blanc |
| Succès flash | #388E3C | blanc |
