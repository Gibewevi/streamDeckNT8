# Stratégie de logs

Chaque interaction, événement, action, refus, erreur ou comportement anormal est écrit
automatiquement dans un fichier, **un fichier par jour et par composant**.

## Où sont les logs

```
%APPDATA%\StreamDeckTrader\logs\
    plugin-2026-07-31.log     ← plugin Stream Deck (Node.js)
    bridge-2026-07-31.log     ← bridge (C# .NET 8)
    addon-2026-07-31.log      ← add-on NinjaTrader 8 (C# .NET 4.8)
```

Soit, en clair : `C:\Users\<vous>\AppData\Roaming\StreamDeckTrader\logs\`.

Les trois fichiers partagent le **même format de ligne** et la **même horloge locale**, donc les
trois fichiers d'une même journée peuvent être triés ensemble pour rejouer une session complète
de bout en bout (touche pressée → bridge → NinjaTrader → retour).

Un nouveau fichier est créé automatiquement au premier événement suivant minuit. Les fichiers de
plus de **30 jours** sont supprimés à ce moment-là. Si un fichier dépasse **25 Mo** dans la même
journée, la suite est écrite dans `plugin-2026-07-31.1.log`, `.2.log`, etc. — la journée n'est
jamais perdue, mais un emballement ne peut pas remplir le disque avec un seul fichier illisible.

## Format d'une ligne

```
2026-07-31 14:23:45.123 | INFO  | plugin | KeyDown | BuyMarket pressed | uuid=…buymarket qty=2 instrument=MNQ 09-26
└─ horodatage local ─┘   │       │        │         │                   └─ contexte clé=valeur ─┘
      (à la ms)          │       │        │         └─ message
                         │       │        └─ catégorie d'événement
                         │       └─ composant : plugin | bridge | addon
                         └─ niveau
```

Une exception ajoute son type et son message sur la ligne, puis la pile d'appels sur les lignes
suivantes, indentées de 4 espaces :

```
2026-07-31 14:23:45.201 | ERROR | addon | Session | Test d'exception | exception=InvalidOperationException message=échec simulé
    System.InvalidOperationException: échec simulé
       à NinjaTrader.NinjaScript.AddOns.StreamDeck.Services.TradingEngine.BuyMarket(…)
```

Les fichiers sont en UTF-8 avec BOM : les accents s'affichent correctement dans le Bloc-notes,
VS Code et `Get-Content`.

## Niveaux

| Niveau  | Usage |
|---------|-------|
| `TRACE` | Trafic répétitif : état republié plusieurs fois par seconde, rafraîchissements de touches inchangées, trames WebSocket brutes. **Pas écrit par défaut.** |
| `DEBUG` | Détail utile au diagnostic : trames de commandes, changement d'affichage d'une touche, apparition/disparition d'une touche. |
| `INFO`  | Le déroulé normal : touche pressée, commande envoyée/acceptée, ordre soumis, position modifiée, connexion établie. |
| `WARN`  | Refus et anomalies **attendues** : macro de sécurité qui bloque, cooldown actif, ordre rejeté par NinjaTrader, timeout, déconnexion. |
| `ERROR` | Exceptions et échecs inattendus, avec pile d'appels. |

Le niveau par défaut des fichiers est `DEBUG`. `TRACE` reste disponible à la demande pour rejouer
le flux WebSocket complet, mais il représente des dizaines de milliers de lignes par jour.

## Catégories d'événements

| Catégorie | Ce qu'on y trouve |
|-----------|-------------------|
| `Session` | Démarrage/arrêt d'un composant, version, **configuration complète du deck** (une ligne par touche, avec ses réglages), chemin du fichier de log |
| `AutoBE` | Automatisme de break-even : armement, seuil atteint, pose, réarmement au renfort, abandon |
| `Navigation` | Changement de page, déclenché depuis le boîtier ou depuis l'interface |
| `Supervisor` | Surveillance du bridge : injoignable, relancé |
| `KeyDown` | Chaque appui sur une touche, avec quantité, instrument, compte et réglages au moment de l'appui |
| `Command` | Commande envoyée, acceptée, refusée (avec le code d'erreur) ou terminée, avec sa durée |
| `Order` | Cycle de vie des ordres côté NinjaTrader : soumis, accepté, exécuté, annulé, **rejeté** |
| `Position` / `Protection` | Changements de position et d'ordres de protection (stops, targets) |
| `Safety` | Macro de sécurité : armement, blocage des entrées, refus de désarmement |
| `Cooldown` | Activation/désactivation et démarrage/fin du cooldown |
| `Connection` | Connexions et pertes de connexion entre les trois composants |
| `Wire` | Trames WebSocket, JSON invalide, réponses tardives, timeouts |
| `Visual` | Ce que chaque touche affiche, journalisé **au changement** uniquement |
| `State` | Transitions d'état : compte, instrument, quantité, NinjaTrader connecté ou non |
| `StatePublish` | Santé de la publication d'état côté NT8 (ticks sautés = deck figé sur des données périmées) |

## Transitions d'état côté hôte

`src/deck-host/src/transitions.ts` compare l'état reçu du bridge au précédent et n'écrit
**qu'aux changements réels**. L'état arrive cinq fois par seconde : le journaliser tel quel
remplirait le fichier, ne rien journaliser rendait le comportement des macros indéchiffrable —
on lisait « break-even posé » sans jamais voir la position qui l'avait déclenché.

| Transition | Catégorie | Niveau |
|---|---|---|
| NinjaTrader connecté / perdu | `State` | INFO / WARN |
| Compte changé — **signalé en WARN si non simulé** | `State` | INFO / WARN |
| Instrument, quantité de travail | `State` | INFO |
| Position ouverte / fermée | `Position` | INFO |
| **Renfort ou réduction** : quantité et prix moyen, avant → après | `Position` | INFO |
| Sens de position inversé | `Position` | INFO |
| Stop posé, déplacé, ou **disparu alors que la position est ouverte** | `Protection` | INFO / WARN |
| Target posé, déplacé, retiré | `Protection` | INFO |
| Macro de sécurité armée / désarmée | `Safety` | INFO |
| **Entrées bloquées** par la macro, avec son motif | `Safety` | WARN |
| Temporisation activée, déclenchée, terminée | `Cooldown` | INFO / WARN |

Le stop qui disparaît **avec** la position est en `DEBUG`, pas en `WARN` : c'est le déroulement
normal d'une sortie, et avertir à chaque fois apprendrait à ignorer l'avertissement le jour où il
compte vraiment.

La configuration du deck est écrite au démarrage, une ligne par touche avec ses réglages. Le
fichier du jour est donc autonome : on sait quelles macros étaient posées sans avoir à retrouver
le `layout.json` de l'époque.

## Ce qui est journalisé automatiquement

- **Toute interaction** : chaque appui de touche, chaque changement de réglage dans le
  Property Inspector, chaque apparition/disparition de touche sur le deck.
- **Toute action** : commande envoyée, enrichie par le bridge, dispatchée par l'add-on, ordre
  soumis à NinjaTrader — avec le `requestId` commun aux trois composants et la durée d'exécution.
- **Tout refus** : validation, doublon de requête, macro de sécurité, cooldown, NinjaTrader
  déconnecté, rejet de l'ordre par le broker — toujours avec le code et le motif.
- **Tout événement** : changement de position, de compte, d'instrument, de quantité, connexions,
  état de la macro de sécurité.
- **Toute erreur** : exception avec type, message et pile d'appels — y compris les exceptions non
  interceptées, les rejets de promesse non gérés et les tâches de fond qui plantent, dans les
  trois composants.
- **Tout comportement anormal** : timeout d'une commande (résultat inconnu), réponse arrivée
  après l'abandon, publication d'état bloquée, P&L indisponible, JSON invalide, exécutable du
  bridge introuvable.

## Réglages

Les trois composants lisent les mêmes variables d'environnement :

| Variable | Effet | Défaut |
|----------|-------|--------|
| `STREAMDECK_TRADER_LOG_DIR` | Répertoire des logs | `%APPDATA%\StreamDeckTrader\logs` |
| `STREAMDECK_TRADER_LOG_LEVEL` | Niveau minimum (`TRACE`…`ERROR`) — plugin et add-on | `DEBUG` |
| `STREAMDECK_TRADER_LOG_RETENTION_DAYS` | Rétention en jours | `30` |

Le bridge accepte en plus ses propres réglages `SDBRIDGE_` (prioritaires) :
`SDBRIDGE_LogDirectory`, `SDBRIDGE_FileLogLevel`, `SDBRIDGE_LogRetentionDays`,
`SDBRIDGE_MaxLogFileSizeMb`.

> Le plugin est lancé par Stream Deck et l'add-on par NinjaTrader : pour qu'une variable les
> atteigne, il faut la définir au niveau de l'utilisateur Windows (`setx`) puis redémarrer
> l'application concernée.

## Scénarios de diagnostic

### « J'ai appuyé, il ne s'est rien passé »

```powershell
Select-String "KeyDown" "$env:APPDATA\StreamDeckTrader\logs\plugin-2026-07-31.log"
```

L'appui est-il enregistré ? Sinon, Stream Deck n'a pas transmis l'événement (touche mal
configurée, plugin planté — chercher `Process` / `Session` dans le même fichier).
S'il l'est, la ligne `Command` juste après donne le verdict : `accepted`, `refused` avec un code,
ou `TIMED OUT` (dans ce dernier cas, **l'ordre a peut-être été passé quand même** : vérifier
`Order` dans le log add-on).

### « Mon ordre a été refusé »

Récupérer le `req=…` de la ligne `Command` du plugin, puis le chercher dans les trois fichiers :

```powershell
Select-String "a1b2c3d4" "$env:APPDATA\StreamDeckTrader\logs\*-2026-07-31.log"
```

Le refus apparaît dans le composant qui l'a décidé : le bridge (validation, macro de sécurité,
cooldown, NT8 déconnecté), l'add-on (contexte introuvable, pas de position, pas de flux de prix)
ou NinjaTrader lui-même (ligne `ORDER REJECTED`, motif du broker : marge, marché fermé…).

### « Le deck affiche des données périmées »

Chercher `StatePublish` dans le log add-on (ticks sautés) et `Connection` dans les trois
fichiers. Un deck figé vient presque toujours d'une connexion perdue ou d'une publication d'état
bloquée, et les deux sont journalisées avec leur horodatage.

### « Ça a planté »

`ERROR` dans les trois fichiers, plus la catégorie `Process` (plugin) et `Session` (add-on,
bridge) qui encadrent chaque démarrage et chaque arrêt. Un fichier qui s'arrête net sans ligne
d'arrêt signale un processus tué.

## Corrélation entre composants

Le `requestId` généré par le plugin est repris tel quel par le bridge (`[REQ:…]`) et par l'add-on
(`[REQ:…]`). Une commande se suit donc de bout en bout par une seule recherche, et les durées
mesurées de chaque côté permettent de situer une lenteur : réseau local, bridge, ou NinjaTrader.
