# deck-host — hôte propriétaire

Remplace **l'application Stream Deck d'Elgato et le plugin**. Pilote le boîtier directement en
USB HID, dessine les touches, gère les appuis, et sert une interface de configuration locale.

Le **bridge** et l'**add-on NT8** sont inchangés : cet hôte est un client du bridge, exactement
comme l'était le plugin.

```
boîtier USB ──► deck-host ──ws://127.0.0.1:8218──► bridge ──ws://127.0.0.1:8219──► add-on NT8
                    │
                    └──► http://127.0.0.1:8220   interface de configuration
```

## Lancer

```bash
npm install
npm run build:all   # publie le bridge dans ./bridge/ puis compile l'hôte
npm start
```

`npm run build` seul ne compile que l'hôte. Le bridge est livré **dans le dossier de l'hôte**
(`./bridge/StreamDeckBridge.exe`) : le dossier de plugin Elgato a vocation à disparaître, et
`supervisor.ts` cherche cet emplacement en premier.

**L'application Stream Deck doit être fermée** : deux processus ne peuvent pas écrire sur le même
périphérique HID. Si elle tourne, l'hôte journalise « Ouverture du boîtier impossible » et retente
toutes les 3 s — l'interface reste utilisable, seul le boîtier manque.

Puis ouvrir <http://127.0.0.1:8220>.

Variables d'environnement : `DECKHOST_UiPort` (8220), `DECKHOST_BridgeUrl`
(`ws://127.0.0.1:8218`), `DECKHOST_BridgePath`, `DECKHOST_LOG_LEVEL` (`DEBUG`).

## Installer à demeure

```powershell
powershell -ExecutionPolicy Bypass -File packaging\install.ps1
```

Copie tout dans `%LOCALAPPDATA%\CockpitNinjaTrader` (moteur Node embarqué compris, pour ne pas
dépendre du `PATH`), puis crée une **tâche planifiée** qui démarre à l'ouverture de session et
**relance l'hôte s'il s'arrête**. Ce filet remplace la relance silencieuse que l'application
Elgato assurait pour le plugin : sans lui, un hôte mort laisse le deck inerte sans rien signaler.

Désactive aussi le démarrage automatique de Stream Deck, qui se disputerait le boîtier — la valeur
est sauvegardée dans `elgato-autostart.bak` et restaurée par `uninstall.ps1`. **L'application
Elgato reste installée : elle seule met à jour le firmware.**

`packaging\uninstall.ps1` annule tout et conserve vos données (`-RemoveData` pour les effacer).

> **Ne pas lancer l'hôte depuis un terminal qui va se fermer.** Il est tué avec son processus
> parent : lancé ainsi, il meurt quelques secondes après la fin de la commande. C'est la raison
> d'être de la tâche planifiée.

## Fichiers

| Module | Rôle |
|---|---|
| `host.ts` | Câblage, appuis, envoi des commandes, temporisation, macro de sécurité |
| `device.ts` | USB HID : ouverture, rendu différentiel, reconnexion, luminosité |
| `catalog.ts` | Les 23 actions et le schéma de leurs réglages — **remplace `manifest.json`** |
| `layout.ts` | Pages et affectations, rechargement à chaud — **remplace les `.sdProfile`** |
| — action `host.navigate` | Changer de page depuis une touche : page suivante, précédente (les deux bouclent) ou page précise. Remplace les touches « dossier » natives d'Elgato, qui n'existent plus. L'interface complète la liste des destinations avec les pages réelles du layout — le catalogue, statique, ne peut pas les connaître |
| `visual-engine.ts` | Port de `computeVisual`, garde-fous compris |
| `server.ts` + `ui/` | Interface de configuration (HTTP + WebSocket, 127.0.0.1 uniquement) |
| `supervisor.ts` | Démarre et surveille le bridge — rôle repris au plugin Elgato |
| `logger.ts` | Journal `%APPDATA%\StreamDeckTrader\logs\host-AAAA-MM-JJ.log` |

Repris **sans modification** du plugin : `visuals.ts`, `messages.ts`, `bridge-client.ts`
(687 lignes). `status-display.ts` en est extrait, débarrassé de la classe morte `BaseAction`.

## Données

`%APPDATA%\StreamDeckTrader\layout.json` — pages, affectations, réglages, luminosité. Éditable à
la main ; l'hôte le relit et redessine sans redémarrage. Un fichier illisible est sauvegardé en
`.invalide-<horodatage>` et remplacé par la configuration initiale, plutôt que de laisser le deck
inerte.

## Pièges

- **`cancelOrders` ferme la position**, `cancelWorkingOrders` n'annule que les ordres en attente.
  Les noms sont contre-intuitifs et viennent de `MessageValidator.KnownActions` ; les confondre
  coûte de l'argent réel.
- **La police est épinglée dans `device.ts`.** Ne pas repasser aux polices système : resvg les
  rescanne à chaque rendu, soit 95 ms par touche au lieu de 2 ms. Et `loadSystemFonts: false`
  seul ne dessine aucun texte.
- **Le superviseur ne sonde jamais en WebSocket**, seulement en TCP refermé aussitôt : le bridge
  n'accepte qu'un client plugin, une sonde ouverte prendrait la place de l'hôte.
- Le bridge n'est **pas** arrêté avec l'hôte : il porte la macro de sécurité et son verrou, qui
  doivent survivre au redémarrage de l'interface.
- **Le visuel `COUNTDOWN` doit dessiner son chiffre en SVG.** Sous Elgato il restait vide, le
  nombre venant de `setTitle` rendu nativement. Sans texte dans le SVG, la temporisation paraît
  figée alors qu'elle tourne. Même piège pour tout visuel qui reposait sur le titre natif.
- **`paintAll` est coalescé** (`painting` / `repaintPending` dans `host.ts`). Six sources le
  déclenchent ; sans cela deux passes concurrentes lisent le cache avant que l'une n'écrive, et
  la même touche part deux ou trois fois sur l'USB.
- **Tout script `.ps1` doit être enregistré avec un BOM UTF-8.** PowerShell 5.1 lit sinon le
  fichier en codepage ANSI : les accents cassent l'analyse syntaxique avec des erreurs qui
  désignent des lignes sans rapport.

## État

Fonctionnel et vérifié :

- boîtier MK.2 piloté sans l'application Elgato (15 touches, 5×3) ;
- les 19 commandes émises sont acceptées par le bridge — vérifié contre une instance isolée,
  aucun `UNKNOWN_ACTION` ;
- superviseur : bridge tué → relancé et reconnecté en 3 s ;
- interface : palette, glisser-déposer, réglages, pages, aperçu live, luminosité ;
- rendu différentiel, reconnexion boîtier et bridge, journal quotidien.

Validé de bout en bout le 31/07/2026 avec NinjaTrader connecté, sur **Sim101** — chaîne complète
boîtier → hôte → bridge → add-on → NT8 :

| Commande | Effet observé |
|---|---|
| `buyMarket` / `sellMarket` | position ouverte, identifiant d'ordre retourné |
| `buyLimit` | « Limit @ 28419,25 » — 40 ticks **sous** le marché : le signe négatif du décalage est bien la convention |
| `cancelOrders` | « Cancelled all orders and closed position » |
| **`cancelWorkingOrders`** | « Cancelled 1 order(s) » — **position `Long 1` intacte, `activeOrderCount` 1 → 0** |
| `reverse` | « Reverse from Long 1 → Sell 2 » → `Short 1` |
| `flatten` | retour à plat |
| `setInstrument` / `setAccount` | appliqués dans NT8 |

La ligne `cancelWorkingOrders` est celle qui compte : elle prouve **par l'effet réel**, et non par
le nom, que la correspondance corrigée est la bonne. L'ancienne aurait liquidé la position.

**Non validés en conditions réelles** : `moveStop`, `moveTarget` et `breakeven` ont été refusés
avec `NO_STOP_ORDER` / `NO_TARGET_ORDER`, faute de stop ou de target attaché à la position. Le
chemin et les codes d'erreur sont donc corrects, mais leur **effet** sur un stop existant reste à
éprouver — il faut une position gérée par une stratégie ATM.

> **Le compte `APEX-346280-33` est un compte financé, et `BridgeConfig.AllowLiveAccounts` vaut
> `true`.** Rien n'empêche un ordre réel si ce compte est sélectionné. Vérifier le compte affiché
> avant d'appuyer, tant que le plan de test n'est pas intégralement rejoué.

Temporisation validée le 31/07/2026 : le bridge l'active 60 s après une position fermée en perte,
le décompte décroît (`20 → 17 s` côté bridge, `32 → 29` réécrit sur le boîtier) et les entrées
sont refusées avec `COOLDOWN_ACTIVE` pendant qu'elle court.

Reste à faire :

- **rejeu complet de `docs/test-plan.md`** — `moveStop`, `moveTarget` et `breakeven` n'ont pas
  encore été éprouvés contre un stop réel (il faut une position gérée par une stratégie ATM) ;
- **lancer `packaging\install.ps1`** — écrit et validé syntaxiquement, mais jamais exécuté : il
  modifie le démarrage automatique et crée une tâche planifiée ;
- débranchement USB à chaud et veille/reprise Windows : le code de reconnexion existe, il n'a pas
  été éprouvé ;
- icône de barre d'état (écartée pour l'instant : elle imposerait une dépendance native, alors
  que la tâche planifiée couvre déjà le démarrage et la relance) ;
- gestes (appui long, accord à deux touches) et changement de page piloté par l'état du trading —
  les deux gains qui justifient le projet, voir `docs/etude-hote-proprietaire.md` §4.1.
