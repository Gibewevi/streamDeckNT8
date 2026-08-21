# Macro « Auto TP/SL »

Pose automatiquement un **take profit** et un **stop loss** dès qu'une position s'ouvre, dans le
sens de celle-ci. C'est la deuxième macro du deck capable d'émettre un ordre sans appui de touche,
après l'[Auto BE](#cohabitation-avec-lauto-be).

| | |
|---|---|
| **Désarmée** | La touche affiche `OFF` et les distances réglées. Rien n'est envoyé |
| **Armée** | Chaque position ouverte reçoit ses protections. Un renfort les fait suivre |

On arme par un **appui simple** sur la touche, comme l'Auto BE — pas de maintien, pas de verrou :
ce n'est pas une limite de risque adossée à un fait comptable, c'est une aide à l'exécution, et le
trader doit pouvoir la relâcher quand il veut gérer ses sorties à la main.

L'armement **survit au redémarrage** (`autotpsl.json` dans le dossier de données du poste). Une
protection que le trader croit armée doit l'être encore après un plantage de l'hôte.

## Ce qui est livré

| Fichier | Rôle |
|---|---|
| `src/NinjaTrader.AddOn.StreamDeck/Services/TradingEngine.cs` | `AttachBracket` — le calcul des niveaux et l'envoi des deux ordres liés en OCO |
| `src/NinjaTrader.AddOn.StreamDeck/Services/CommandDispatcher.cs` | Routage de `attachBracket` |
| `src/StreamDeckBridge/MessageValidator.cs` | Action connue, bornes des deux distances |
| `src/deck-host/src/host.ts` | L'automatisme : `evaluerAutoTpSl`, armement, persistance |
| `src/deck-host/src/catalog.ts`, `visual-engine.ts`, `visuals.ts` | La touche, ses deux réglages et son visuel |

## Les deux réglages

| Réglage | Plage | Sens |
|---|---|---|
| **Take Profit (ticks)** | 0–10000 | Distance depuis le prix moyen, **au-dessus** en long, **en dessous** en short |
| **Stop Loss (ticks)** | 0–10000 | Distance depuis le prix moyen, **en dessous** en long, **au-dessus** en short |

`0` sur un champ veut dire **« cette jambe n'est pas posée »**, et les deux valent 0 par défaut :
une macro qui poserait des protections inventées dès qu'on la dépose sur une touche enverrait des
ordres que personne n'a réglés.

Les quatre combinaisons demandées sont exactement celles-ci :

| TP | SL | Résultat |
|---|---|---|
| 0 | 0 | Rien n'est posé. **La touche refuse même de s'armer** et affiche `REGLER` |
| >0 | 0 | Take profit seul |
| 0 | >0 | Stop loss seul |
| >0 | >0 | Les deux, **liés en OCO** |

Refuser l'armement à 0/0 est délibéré : une touche orange annonçant une macro armée qui n'enverra
jamais rien est le piège classique de l'interrupteur sans effet — le trader la règle, ne voit rien
se produire, et conclut que le logiciel ne marche pas. Le refus est journalisé avec la correction à
apporter.

## Pourquoi les niveaux partent APRÈS l'entrée, et non avec elle

`Account.Submit` est **asynchrone** : il rend la main avant l'exécution. Un prix d'entrée lu à cet
instant serait une supposition — sur un ordre au marché en séance rapide, il peut manquer plusieurs
ticks, et sur un ordre limite partiellement exécuté il n'a aucun sens.

La macro attend donc que la position **existe**, et lit son `averagePrice` : la seule valeur qui
soit vraie. L'hôte reçoit l'état toutes les 200 ms, ce qui borne le délai entre l'exécution et la
pose des protections — c'est la fenêtre pendant laquelle la position est nue, et c'est aussi
pourquoi l'évaluation tourne à chaque publication d'état plutôt que sur un minuteur à part.

Cette lecture du prix moyen a un second effet, gratuit : **un renfort de position est géré sans
aucun cas particulier**. Le prix moyen bouge, l'hôte le voit, il renvoie la même commande, et
l'add-on repositionne *et redimensionne* les deux jambes sur le nouveau prix moyen et la nouvelle
quantité.

L'hôte mémorise aussi **les distances** avec lesquelles il a posé, pas seulement le prix moyen :
corriger un réglage en séance agit donc sur la **position en cours**. Sans cela, le trader
modifiait son stop dans l'éditeur, voyait la touche annoncer `POSE`, et ne découvrait qu'au trade
suivant que l'ancienne valeur s'appliquait toujours.

## Le lien OCO n'est pas un détail

Les deux jambes partent **dans un seul `Account.Submit`, sous le même identifiant OCO**.

Sans lui, un take profit exécuté laisse le stop actif alors que la position est à plat. Et un stop
sur une position à plat n'est plus une protection : c'est une **entrée**, qui ouvre le trade inverse
de celui que l'on venait de clôturer, sans que personne ne l'ait demandé.

Sur un renfort, l'identifiant OCO du groupe existant est **réutilisé** : `Account.Change` ne sait
pas réécrire un OCO, et créer la seconde jambe dans un groupe à elle laisserait les deux déliées.

## Ce que la macro ne fait jamais

- **Elle n'ajoute pas une seconde protection du même côté.** Un stop déjà en place — stratégie ATM,
  stop manuel, break-even — laisse la jambe à `kept:foreign`. La position est déjà couverte, et un
  second stop sortirait du double de la taille avant d'ouvrir le trade inverse.
- **Elle ne touche pas aux ordres du trader.** Les siens portent les noms `StreamDeck_SL` et
  `StreamDeck_TP` ; tout le reste est laissé intact. Déplacer en silence un stop posé à la main
  retirerait la seule protection que le trader ait choisie lui-même.
- **Elle ne pose pas un niveau déjà franchi** (`refused:pastMarket`). L'ordre serait immédiatement
  exécutable : il ne protégerait pas le trade, il le clôturerait sur-le-champ, au prix du marché.
  Le cas ne se produit pas dans le flux normal — il apparaît si l'on arme la macro sur une position
  déjà largement en gain ou en perte.
- **Elle ne confond pas une entrée avec un objectif.** `FindTargetOrders` remonte *tous* les ordres
  limites de l'instrument, et un achat limite posé sous une position longue est une entrée. Seuls
  les ordres qui **clôturent** la position sont examinés ; sans ce filtre, une limite d'entrée en
  attente aurait convaincu la macro que le trade avait déjà son take profit.

Une seule chose est annulée d'office : **ses propres jambes restées d'une position de sens opposé**.
Le stop d'un long est un `Sell` ; sur le short qui suit, il ajoute à la position au lieu d'en sortir.
Le cas arrive dès qu'on retourne au marché sans passer par la touche *Inverser*, qui annule d'abord.

## Cohabitation avec l'Auto BE

Les deux macros sont complémentaires et peuvent être armées ensemble :

1. **Auto TP/SL** protège **immédiatement** : le stop part avec la position ;
2. **Auto BE** attend un **gain** et remonte ce même stop au point mort.

L'Auto BE trouve le stop posé ici (`FindStopOrders` ne regarde pas le nom) et le **déplace** au lieu
d'en créer un second. C'est le comportement voulu.

Sur un renfort, les deux se réarment sur le nouveau prix moyen : l'Auto TP/SL repose le stop à sa
distance d'origine, et l'Auto BE le remontera quand le nouveau seuil sera atteint. Un break-even
calculé sur l'ancien prix moyen n'en était de toute façon plus un.

## Ce que montre la touche

| Affichage | État |
|---|---|
| `TP/SL` · `OFF` · `TP40 SL20` | Désarmée. Les distances réglées restent lisibles |
| `TP/SL` · `ARME` · `TP40 SL20` | Armée, en attente d'une position |
| `TP/SL` · `POSE` · `TP40 SL20` | Les protections sont posées sur la position en cours |
| `TP/SL` · `REGLER` · `aucune distance` | Armée sans aucune distance — rien ne sera posé |

`--` remplace la valeur d'une jambe désactivée (`TP40 SL--`) : sur une touche, un `0` se lit comme
une distance réglée à zéro alors qu'il veut dire « pas de protection de ce côté ».

## En cas d'échec

L'automatisme retente toutes les 2 s, **cinq fois au plus**, puis abandonne pour la durée de la
position — marteler NinjaTrader avec un ordre qu'il refuse n'a jamais rien posé.

L'abandon écrit une ligne explicite dans le journal du jour :

```
AutoTPSL | Abandon après échecs répétés — POSITION SANS PROTECTION AUTOMATIQUE
```

C'est le pire état que cette macro puisse produire : une position ouverte que la touche annonce
protégée et qui ne l'est pas. Elle doit se retrouver en relisant le fichier, sans avoir à croiser
quoi que ce soit.

## Limites assumées

- **La fenêtre entre l'exécution et la pose.** Deux cents millisecondes au plus dans le cas normal,
  davantage si NinjaTrader tarde à publier la position. Elle est irréductible sans deviner le prix
  d'entrée, ce que la macro refuse de faire.
- **`Account.Submit` reste asynchrone.** La réponse dit que les ordres sont partis, pas qu'ils sont
  acceptés. Un rejet (marge, marché fermé) arrive plus tard par `orderUpdate` et s'affiche sur les
  touches d'entrée.
- **Rien n'est posé sans position.** Une entrée limite en attente n'est pas protégée — il n'y a rien
  à protéger tant qu'elle n'est pas exécutée.
- **Une jambe refusée pendant que l'autre passe n'est pas reprise.** Si le take profit est déjà
  franchi au moment de la pose mais que le stop, lui, est plaçable, la commande réussit : la
  position part avec son stop et sans objectif, et l'avertissement `Take profit NOT placed` est le
  seul témoin. L'automatisme ne réessaiera qu'au prochain changement de prix moyen. Le cas suppose
  d'armer la macro sur une position déjà au-delà de son objectif ; il ne se produit pas dans le flux
  normal, où les deux jambes encadrent le prix d'entrée.
- **L'éditeur Bitlearn ne montre pas encore l'armement.** Il recopie le rapport d'état champ par
  champ et ignore `autoTpSl` tant qu'il n'a pas été mis à jour de son côté ; le visuel s'affiche
  alors au repos. Le boîtier, lui, est juste.
