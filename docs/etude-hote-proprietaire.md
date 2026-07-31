# Étude — hôte propriétaire en remplacement de l'application Stream Deck

**Date : 31 juillet 2026.** Étude de faisabilité, pas une décision d'engagement.

Question posée : développer un exécutable propriétaire qui installe l'extension, pilote
directement le boîtier et fournit une interface minimaliste de gestion des macros, en supprimant
la dépendance à l'application Stream Deck d'Elgato.

Périmètre retenu avec l'utilisateur :

- le boîtier sert **exclusivement** au cockpit NinjaTrader — aucun plugin tiers, aucune macro
  externe à réimplémenter ;
- motivations invoquées : contrôle et indépendance, performance et latence, personnalisation
  impossible aujourd'hui, friction de déploiement ;
- matériel : **Stream Deck MK.2** (VID `0x0FD9` / PID `0x0080`, 15 touches 5×3, ni molette ni
  écran tactile), avec ouverture souhaitée aux **XL / Mini / Neo**. Le Stream Deck+ et ses
  encodeurs sont hors périmètre.

---

## 1. Ce qui est réellement en jeu

L'analyse du code donne un résultat décisif : **la surface de contact avec Elgato est minuscule**.

Sur les 2 562 lignes de TypeScript du plugin — dont environ 460 sont déjà mortes (voir
`src/streamdeck-ninjatrader/CLAUDE.md`) — la totalité du couplage à l'écosystème Elgato tient en :

| Ce qu'Elgato fournit | Utilisé | Où |
|---|---|---|
| 4 événements : `willAppear`, `willDisappear`, `didReceiveSettings`, `keyDown` | oui | `plugin.ts:519-570` |
| 4 commandes : `setImage`, `setTitle`, `showOk`, `showAlert` | oui | `plugin.ts:244-251` |
| Handshake `-port` / `-pluginUUID` / `-registerEvent` / `-info` | oui, entièrement délégué au SDK | `plugin.ts:1075` |
| Persistance des réglages par touche et Property Inspectors | oui | `ui/*.html`, `ui/pi.js` |
| États multiples, profils, molettes, écran tactile, réglages globaux, `sendToPlugin`, deep links, multi-actions, `switchToProfile` | **non** | — |

Tout le reste — le rendu, la logique métier, l'état, la sécurité — nous appartient déjà. Les
touches sont **déjà** dessinées en SVG maison (`utils/visuals.ts` : 144×144, huit layouts, palette
de 24 couleurs) puis poussées en data URI. Les icônes PNG déclarées au manifeste ne sont jamais
visibles sur une touche en fonctionnement : elles ne servent qu'à la palette de l'éditeur Elgato.

Surtout : **le bridge (2 487 lignes) et l'add-on NT8 (3 433 lignes) sont neutres.** Une recherche
exhaustive ne trouve dans le bridge que trois chaînes cosmétiques mentionnant Stream Deck
(`BridgeServer.cs:130` et `:250`, plus des commentaires dans `SafetyMacro.cs`). Aucune notion de
`setImage`, de contexte de touche, de coordonnées, de profil ni d'appareil. Le protocole
(`docs/protocol.md`) est une enveloppe JSON pure, sans concept d'affichage.

> **Les deux tiers du système, dont toute la partie critique pour la sécurité, survivent au
> remplacement sans une ligne modifiée.**

**Conséquence majeure sur le périmètre : il ne s'agit pas de construire une plateforme à
plugins.** Il n'y a qu'un seul « plugin » — le nôtre — et il devient un simple module de l'hôte.
Cela retire d'emblée l'architecture de chargement, l'isolation par processus, le registre
d'actions, la place de marché et la compatibilité SDK : c'est-à-dire l'essentiel de ce qui rend
l'application Elgato coûteuse à écrire.

---

## 2. Faisabilité technique

### 2.1 Le point dur, et pourquoi il ne l'est pas

Le seul verrou réel est la communication USB avec le boîtier. Le protocole HID des Stream Deck est
**documenté et implémenté par des bibliothèques open source matures** :

| Bibliothèque | Langage | Licence | Modèles couverts |
|---|---|---|---|
| `@elgato-stream-deck/node` | Node / TS | MIT | Original, V2, **MK.2**, **Mini**, **XL**, **Neo**, Pedal, Plus |
| `StreamDeckSharp` / OpenMacroBoard | C# | MIT | Original, V2, MK.2, Mini, XL |
| `python-elgato-streamdeck` | Python | LGPL | idem |

**Il n'y a rien à rétro-ingénierer.** Le protocole se résume à des rapports d'entrée HID (un octet
par touche) pour les appuis, des rapports de sortie fragmentés pour les images, et des rapports de
fonctionnalité pour la luminosité, la réinitialisation et le numéro de série. Il est stable depuis
plusieurs générations de matériel.

C'est le facteur qui change tout dans cette étude : sans ces bibliothèques, le projet serait
déraisonnable. **Faisabilité technique élevée, risque faible.**

### 2.2 Les vraies difficultés, par ordre décroissant

1. **Cohabitation avec l'application Elgato.** Deux processus ne peuvent pas écrire sur le même
   périphérique HID. `StreamDeck.exe` démarre automatiquement (clé de registre `Run`) et doit être
   neutralisé. L'application reste néanmoins **installée** : elle seule sait mettre à jour le
   firmware du boîtier.
2. **Rasterisation SVG.** Aujourd'hui c'est l'application Elgato qui convertit notre SVG en image.
   Notre hôte doit le faire lui-même : `@resvg/resvg-js` (Rust, binaires précompilés win32-x64)
   produit un tampon RGBA que `fillKeyBuffer()` encode en JPEG. **Piège mesuré, voir §11 : par
   défaut resvg rescanne les polices système à chaque instanciation, ce qui coûte ~95 ms par
   touche.** Il faut épingler un fichier de police explicite pour retomber à ~2 ms.
3. **Cycle de vie USB.** Débranchement et rebranchement, veille et reprise de Windows,
   ré-énumération du périphérique. La bibliothèque expose l'énumération, mais la reconnexion et le
   redessin intégral restent à notre charge. **C'est ici que se logeront les bugs de production.**
4. **Résilience.** Aujourd'hui l'application Elgato relance silencieusement le plugin après un
   crash (voir `utils/logger.ts:290`). Ce filet disparaît et doit être reconstruit sous forme de
   supervision explicite.

---

## 3. Ce qu'il faudrait recréer

| # | Fonction | Difficulté | Remarque |
|---|---|---|---|
| 1 | Transport HID : appuis, images, luminosité, reset, reconnexion | **Faible** | bibliothèque existante |
| 2 | Pipeline de rendu SVG → RGBA → périphérique | Faible | `visuals.ts` réutilisé tel quel |
| 3 | Modèle de layout, en remplacement du `.sdProfile` : pages × emplacements × action × réglages | Faible | JSON ; 15 touches sur 3 pages aujourd'hui |
| 4 | Navigation entre pages | Faible | assurée aujourd'hui par les touches « dossier » natives d'Elgato ; devient une pseudo-action `navigate` |
| 5 | Persistance des réglages par touche | Faible | fichier JSON |
| 6 | Formulaires de réglages par action | Moyenne | les 7 Property Inspectors existants sont du HTML autonome, sans composants SDPI — récupérables presque tels quels |
| 7 | **Interface de configuration** : grille, palette d'actions, édition, aperçu | **Élevée** | poste le plus lourd ; voir §7 |
| 8 | Cycle de vie : exécutable unique, icône de barre d'état, démarrage automatique, instance unique, propriété du bridge, watchdog | Moyenne | le plugin lance déjà le bridge en *fire-and-forget* sans jamais l'arrêter (`plugin.ts:1039-1062`) — à assainir |
| 9 | Installeur : déploiement, démarrage automatique, neutralisation de l'auto-démarrage Elgato, désinstallation | Moyenne | Inno Setup |
| 10 | Abstraction multi-modèles : XL / Mini / Neo, taille de grille et format d'image | Faible | couverte par la bibliothèque ; seul le modèle de layout doit connaître la grille |

**Inutile de recréer** : états multiples, profils Elgato, molettes et écran tactile, réglages
globaux, `sendToPlugin`, multi-actions, deep links, et les icônes PNG hors palette de notre propre
éditeur.

---

## 4. Avantages

### 4.1 Réels et importants

**Personnalisation.** C'est le gain le plus fort, et il est structurel. Deviennent possibles :

- **le changement de page piloté par l'état du trading** — basculer automatiquement sur la page
  « en position » au fill, sur la page « verrouillée » quand la macro de sécurité se déclenche.
  Impossible avec Elgato, où un profil ne change que sur action utilisateur ou changement de
  focus applicatif. *À lui seul, ce point peut justifier le projet ;*
- **les gestes** : appui long pour confirmer un Flatten, accord à deux touches pour armer un
  compte réel. Elgato ne fournit que `keyDown` et `keyUp` par touche isolée. Sur un cockpit de
  trading, c'est un gain de **sécurité**, pas de confort ;
- **le rendu multi-touches** — une barre de P&L étalée sur cinq touches, une échelle de position —
  les animations réelles, et une cadence de rafraîchissement propre à chaque touche (un P&L à
  10 Hz, une horloge à 1 Hz) au lieu d'un tic global unique ;
- **la luminosité pilotée par l'état** : atténuée à plat, vive en position.

**Friction de déploiement.** Disparaissent : le redémarrage de Stream Deck après chaque build, le
manifeste relu au seul démarrage, l'impossibilité de recharger à chaud, le plugin relancé en
silence sans laisser de trace. Le rechargement d'un layout devient instantané. Le gain porte sur
chaque itération de développement, mais **il est nul pendant le trading**.

**Contrôle et indépendance.** Fin de la dépendance à un éditeur tiers, à ses migrations de SDK — la
v1 vers v2 a déjà cassé des plugins — et à ses mises à jour automatiques. Réel, mais à faible
fréquence d'occurrence.

**Simplicité de l'installation finale.** Un exécutable, au lieu de « installer Stream Deck, copier
un dossier de plugin, redémarrer l'application ».

### 4.2 Surévalué : la performance

**La latence n'est pas un argument valable pour ce projet.**

La mesure sur 37 ordres réels (commit `87c0a35`) montre que le chemin de commande est déjà au
plancher : 0 ms médian côté bridge, 1 ms côté add-on. Le délai perçu à l'affichage vient
d'ailleurs :

```
fill NT8 ──100 ms──► OrderMonitor.PublishNow ──0-1 ms──► bridge ──[jusqu'à 2000 ms]──► plugin
                                                                         ▲
                                          BridgeConfig.StateUpdateIntervalMs = 2000
```

Le goulot dominant est la **diffusion du bridge vers l'interface, réglée à 2 000 ms**
(`Models/BridgeConfig.cs:14`, boucle en `BridgeServer.cs:344`). C'est un paramètre de
configuration **sans aucun rapport avec Elgato**, surchargeable par
`SDBRIDGE_StateUpdateIntervalMs`.

Le gain réellement imputable au remplacement d'Elgato se limite à la suppression d'un saut
WebSocket sur le chemin `setImage` : quelques millisecondes. Négligeable.

> **À faire immédiatement, indépendamment de toute décision sur cette étude :** abaisser
> `StateUpdateIntervalMs` de 2000 à environ 200 ms, puis mesurer. Coût : une variable
> d'environnement. Gain attendu : environ 10× sur le délai d'affichage après un fill —
> c'est-à-dire l'essentiel de ce que la motivation « performance » recherche, pour zéro ligne de
> code.

Un gain secondaire mais réel existe tout de même côté hôte : aujourd'hui **toutes** les touches
visibles sont ré-encodées et repoussées à chaque cycle (`plugin.ts:255-259`), car
`lastVisualSignature` ne filtre que la journalisation, pas l'envoi. Un hôte propriétaire
n'enverrait que les touches effectivement modifiées.

---

## 5. Inconvénients et risques

| Risque | Gravité | Atténuation |
|---|---|---|
| **Panne de l'hôte en position ouverte** : toutes les touches figées, plus de Flatten | **Critique** | Exigence non négociable — le boîtier ne doit **jamais** être l'unique moyen de sortir d'une position ; chart et DOM NinjaTrader restent disponibles. Plus watchdog, redémarrage automatique et visuel « hôte hors service ». |
| Bugs USB en production : veille, débranchement, ré-énumération | Élevée | Classe de bug la plus probable. Tests explicites veille/reprise et débranchement à chaud. |
| Perte de la relance automatique du plugin par Elgato | Moyenne | À reconstruire ; la supervision et la journalisation durable existent déjà. |
| Mise à jour du firmware du boîtier | Faible | Conserver l'application Elgato installée, simplement retirée du démarrage automatique. |
| Maintenance assumée en propre à vie | Moyenne | Estimée à 1–2 jours par trimestre après la v1 : Windows, Node, nouveaux modèles, queue de cas limites USB. |
| Nouveau modèle matériel non couvert | Faible | La bibliothèque couvre déjà XL, Mini, Neo et Plus. |
| Rupture de protocole côté Elgato | Faible | Protocole stable sur plusieurs générations ; risque réel mais lointain. |
| Absence de tests automatisés | Moyenne | Déjà le cas aujourd'hui (`docs/test-plan.md`, 371 lignes de scénarios manuels). Un hôte propriétaire **améliore** ce point : la logique devient testable hors matériel, ce qui est impossible aujourd'hui puisque le SDK lit `manifest.json` dès l'import. |
| Dérive du périmètre sur l'éditeur graphique | **Élevée** | Principal risque budgétaire du projet ; voir §7. |

---

## 6. Estimation de charge

Hypothèse : un développeur expérimenté assisté de Claude Code, travaillant en parallèle de
l'activité de trading. Langage retenu : **Node / TypeScript** (justification en §8).

| Lot | Contenu | Charge |
|---|---|---|
| **0** | **Spike** : ouvrir le MK.2 application Elgato fermée, pousser un SVG rendu, lire un appui | **0,5 – 1 j** |
| **1** | Hôte sans interface, à parité fonctionnelle : portage de la logique existante (`computeVisual`, `bridge-client`, `logger`, `visuals` — environ 1 400 lignes éprouvées reprises quasi telles quelles), `layout.json` statique des 15 touches, rendu différentiel, appuis, retours OK et alerte, reconnexion et veille, propriété du bridge, barre d'état, instance unique | **4 – 6 j** |
| **2** | Interface de configuration : serveur local HTTP + WS, éditeur de grille, palette d'actions, portage des 7 formulaires de réglages, gestion des pages, aperçu, application à chaud | **6 – 10 j** |
| **3** | Empaquetage : exécutable, installeur Inno Setup, démarrage automatique, neutralisation de l'auto-démarrage Elgato, désinstallation, assistant de premier lancement | **2 – 4 j** |
| **4** | Durcissement : rejeu du plan de test manuel, débranchement à chaud, veille Windows, watchdog, visuel « périphérique perdu », abstraction XL/Mini/Neo, réécriture de la documentation | **4 – 6 j** |
| | **Total** | **17 – 27 j-homme** |

Avec une réserve de 25 % pour les aléas USB et Windows : **4 à 8 semaines calendaires** en
travaillant à côté du trading.

---

## 7. Le levier décisif : ne pas construire l'éditeur

Le lot 2 représente **35 à 40 % de la charge totale**. Il concerne un utilisateur unique, dont le
layout est stable — 15 touches sur 3 pages, inchangé depuis des mois — et qui le modifiera
peut-être deux fois par an.

Un fichier `layout.json` versionné, édité à la main ou par Claude Code, avec rechargement à chaud,
couvre ce besoin **immédiatement et mieux** qu'un éditeur graphique : c'est diffable,
versionnable, sauvegardable, et cela ne coûte presque rien à écrire.

En repoussant le lot 2 jusqu'à ce que le layout se mette réellement à bouger :

> **Périmètre v1 = lots 0, 1, 3 et 4 → 10 à 17 j-homme**, pour la quasi-totalité de la valeur.

C'est le résultat le plus important de cette étude.

---

## 8. Choix technique : Node/TS plutôt que consolidation en C#

Fusionner l'hôte dans le bridge C# — un seul exécutable, `StreamDeckSharp` et `SkiaSharp`, Node
supprimé, trois processus ramenés à deux — est séduisant mais doit être écarté, pour deux raisons :

1. **Réutilisation.** En Node/TS, environ 1 400 lignes de logique de rendu et d'état éprouvées en
   production sont transférées mécaniquement. En C#, il faudrait les réécrire — et dans un outil
   de trading, le risque est dans la réimplémentation d'un comportement, pas dans le nombre de
   processus.
2. **Isolation.** Le bridge porte la macro de sécurité et le cooldown, et se relance seul après un
   crash (`RunWithRestart`, `BridgeServer.cs:94`). Y fusionner le rendu ferait qu'un bug
   d'affichage pourrait emporter l'application des règles de sécurité. **La séparation des
   processus est une propriété du système, pas un défaut.**

Le bridge et l'add-on NT8 ne sont donc **pas touchés**. Seul le plugin est remplacé.

---

## 9. Conclusion

**Le projet apporte une plus-value réelle et il est nettement moins coûteux qu'il n'y paraît —
mais pas pour les raisons initialement avancées.**

Ce qui le rend raisonnable :

- la surface de contact avec Elgato se limite à **4 événements et 4 commandes** ;
- le protocole USB est couvert par des bibliothèques open source matures : **rien à
  rétro-ingénierer** ;
- il n'y a **qu'un seul plugin à héberger**, donc aucune plateforme à construire ;
- **les deux tiers du système — bridge et add-on — survivent intacts** ;
- le boîtier ne sert qu'au trading : aucune fonctionnalité tierce à réimplémenter.

Ce qui justifie de le faire : la **personnalisation** — pages pilotées par l'état du trading,
gestes de confirmation à valeur de sécurité, rendu multi-touches — et, accessoirement, la fin de
la friction de déploiement et de la dépendance à un éditeur tiers.

Ce qui ne le justifie pas : **la performance**. Le principal délai perçu vient d'un paramètre du
bridge, corrigeable immédiatement et gratuitement.

### Recommandation

1. **Immédiatement, sans lien avec ce projet** — abaisser `SDBRIDGE_StateUpdateIntervalMs` de 2000
   à environ 200 ms, puis mesurer. C'est probablement 90 % du gain de réactivité recherché.
2. **Lancer le lot 0** (spike, au plus 1 jour). Décisif et quasi gratuit : il valide en une
   journée l'unique hypothèse risquée du projet.
3. **Si le spike passe, engager le lot 1** (environ 1 semaine). Il délivre l'indépendance et la
   fin de la friction de déploiement, et il est **réversible** : le plugin Elgato reste sur le
   disque, il suffit de réactiver l'application pour revenir en arrière.
4. **Ne pas construire l'éditeur graphique** tant que le layout ne bouge pas réellement.
5. **Poser comme exigence non négociable** que le boîtier ne soit jamais l'unique moyen de sortir
   d'une position.

En résumé : **oui au remplacement, en version dégraissée et par étapes réversibles** — non au
grand projet de réécriture d'un écosystème complet.

---

## 11. Résultats du spike (lot 0) — exécuté le 31 juillet 2026

Le lot 0 a été réalisé le jour même de l'étude, application Elgato fermée, NinjaTrader non lancé,
aucune position ouverte. Le spike importe et exécute le **vrai** `dist/visuals.js` déployé : il
valide le pipeline de production, pas une maquette.

### Ce qui est validé

```
[2] détecté : original-mk2   VID=0x0FD9  PID=0x0080
[3] ouvert  : Stream Deck MK.2 — 15 touches, 72x72 px, NFC=false
[4] 15 touches rendues et poussées
    envoi USB : médiane 1,5 ms   max 8,6 ms
```

- **Prise de contrôle du matériel sans l'application Elgato : confirmée.** Détection, ouverture,
  luminosité, effacement, rendu des 15 visuels de trading réels, fermeture propre avec
  restauration du logo Elgato.
- **Envoi USB : 1,5 ms médian par touche.** Non problématique.
- `@elgato-stream-deck/node` 7.6.3 s'installe **sans compilation native** (binaires précompilés).
- Le modèle est bien identifié `original-mk2`, ce qui confirme l'hypothèse matérielle de l'étude.
- **`fillPanelBuffer()` existe en API de première classe** : le rendu multi-touches annoncé en
  §4.1 est natif, il n'est pas à bricoler.

### Ce qui invalide une affirmation de cette étude

Le §2.2 annonçait un coût de rasterisation « négligeable ». **C'est faux en configuration par
défaut.** Mesures sur le premier appel puis 30 rendus du même SVG (389 octets) :

| Configuration | Médiane | Texte rendu ? |
|---|---|---|
| A. par défaut (polices système) | **94,83 ms** | oui (202 px) |
| B. `loadSystemFonts: false` | 0,04 ms | **non — 0 px** |
| C. `loadSystemFonts: false` + `fontFiles: ['arial.ttf']` | **1,93 ms** | oui (115 px) |

resvg rescanne l'intégralité des polices Windows **à chaque instanciation**. D'où les 96 ms
médians et les 621 ms du premier appel observés au spike, et un rafraîchissement complet du deck
à **2 000 ms** — soit exactement la cadence que l'étude reprochait au bridge.

L'option B est un piège : elle est rapide parce qu'elle ne dessine aucun texte.

**Contrainte d'implémentation qui en découle, à porter dans le lot 1 :** épingler un fichier de
police explicite et désactiver le scan système. On retombe alors à ~2 ms par touche, soit **~29 ms
pour un rafraîchissement complet des 15 touches**, et ~6 ms en rendu différentiel (1 à 3 touches
modifiées par cycle, cas courant). La conclusion de l'étude tient — mais elle ne tenait pas avec
la configuration par défaut.

Point secondaire à traiter au lot 1 : Arial ne donne pas les mêmes métriques que le `sans-serif`
résolu par Elgato (115 px de texte contre 202). Les visuels seront légèrement plus fins ; il
faudra choisir la police de référence (`segoeui.ttf` ou `arialbd.ttf`) et la comparer à l'existant.

### Détection des appuis et boucle complète — validées

Seconde passe, correctif de police appliqué, opérateur devant le boîtier :

```
Rafraîchissement complet AVEC police épinglée : 55,8 ms   (2000,7 ms sans le correctif)
    ↓ touche 11 (col 1, ligne 2) — repeinte en 4,0 ms
    ↓ touche 7  (col 2, ligne 1) — repeinte en 3,3 ms
    …
Touches distinctes pressées : 8
Appuis totaux détectés      : 46
Latence appui → repeint     : 3,0 ms médian
Verdict : LOT 0 VALIDÉ
```

Les 3,0 ms couvrent **la boucle entière** : lecture du rapport HID, décodage, calcul du visuel,
rasterisation SVG, encodage et réécriture USB. À comparer au chemin actuel, où un appui ne peut
produire aucun retour visuel d'état avant le tic de diffusion du bridge.

Le décodage de la bibliothèque a été audité au passage : `KEY_DATA_OFFSET = 3` pour la gen2, soit
l'octet absolu `4 + index` — ce qui correspond exactement à la position observée dans le dump HID
brut. Rien à corriger.

**Le critère de succès du lot 0 est donc rempli intégralement.**

### Chiffres de référence à retenir

| Mesure | Valeur | Remarque |
|---|---|---|
| Envoi USB par touche | 1,5 ms médian | plancher matériel |
| Rasterisation par touche, police épinglée | ~2 ms | contre ~95 ms par défaut |
| Rafraîchissement complet des 15 touches | **55,8 ms** | contre 2 000,7 ms sans le correctif |
| Appui → repeint de la touche | **3,0 ms médian** | boucle complète |

### Reproduire

Le spike est conservé dans le répertoire de travail temporaire de la session : `spike.mjs`
(première passe), `spike2.mjs` (police épinglée et retour visuel à l'appui), `bench-font.mjs`
(comparaison des trois configurations de police), `raw-hid.mjs` (lecture HID brute, utile pour
départager un problème matériel d'un problème de code). Ils sont **hors du dépôt**, afin que
`node_modules` ne se retrouve pas dans `git status` en l'absence de `.gitignore`.

Note de méthode : l'absence d'appuis détectés lors des deux premières passes venait uniquement de
ce que personne n'appuyait sur le boîtier. `raw-hid.mjs` a permis de le prouver — il montre les
rapports HID arriver sous la bibliothèque — avant de suspecter le code à tort. Ce script vaut
d'être conservé pour le lot 4.

---

## 12. Lot 1 réalisé — 31 juillet 2026

L'hôte existe et tourne : `src/deck-host/` (voir son `README.md`). Il pilote le MK.2 sans
l'application Elgato, sert son interface de configuration sur `http://127.0.0.1:8220`, et démarre
le layout sur la transcription exacte du profil Elgato qui était en service.

### Décision revue : l'éditeur graphique est construit

Le §7 recommandait de ne **pas** développer l'interface de configuration et de piloter un
`layout.json` à la main, pour économiser 35 à 40 % de la charge. L'utilisateur l'avait demandée
explicitement dès l'instruction initiale et a maintenu ce choix. Elle fait donc partie du produit.
Le `layout.json` reste éditable à la main : les deux approches coexistent sans surcoût.

### Vérification des correspondances de commandes

Le portage initial des 23 handlers comportait **huit erreurs**, dont une dangereuse : la touche
« Cancel Orders » était mappée sur `cancelOrders`, qui **ferme la position**, au lieu de
`cancelWorkingOrders`. Les sept autres portaient sur des noms inventés (`closePosition`,
`adjustBreakeven`, `setQuantity`, `selectInstrument`, `cycleAccount`) — rejetés sans danger — et
sur les décalages par défaut des ordres limite (−2 / +2, et non 4).

Corrigé, puis **vérifié contre un bridge isolé** (ports 9318/9319, état en dossier temporaire) :
les 19 commandes émises par l'hôte passent la validation, aucun `UNKNOWN_ACTION`. Les 11 actions
d'ordre atteignent `NT_DISCONNECTED`, ce qui prouve que seul l'absence de NinjaTrader les arrête ;
les 8 actions locales s'exécutent.

> Ce contrôle valide les **noms et charges utiles**, pas encore l'**effet réel** d'un ordre. Le
> rejeu de `docs/test-plan.md` avec NinjaTrader connecté reste indispensable avant tout usage en
> séance.

### Supervision du bridge

Le plugin Elgato lançait le bridge en fire-and-forget et ne le surveillait jamais. En supprimant
l'application Stream Deck, **plus personne ne le démarrait** — trou fonctionnel corrigé par
`supervisor.ts`. Éprouvé : bridge tué de force, détecté, relancé et reconnecté en **3 secondes**.

La sonde est une ouverture TCP refermée aussitôt, jamais une WebSocket : le bridge n'accepte qu'un
seul client plugin, et une sonde ouverte prendrait la place de l'hôte — c'est la même raison qui
interdit de sonder le port 8218 pendant que Stream Deck tourne.

---

## Annexe — feuille de route d'exécution

### Étape 0 — Mesure préalable, avant tout code

Lancer un bridge de test isolé (ports 9318/9319 et chemins d'état dédiés, voir `CLAUDE.md`) avec
`SDBRIDGE_StateUpdateIntervalMs=200`, mesurer le délai fill → affichage sur le deck, consigner le
résultat.

### Étape 1 — Spike matériel — **fait et validé le 31 juillet 2026, voir §11**

Énumération HID, ouverture, luminosité, rendu des 15 visuels réels, détection des appuis, boucle
complète appui → repeint en 3,0 ms, fermeture propre. Le lot 0 est clos ; le lot 1 peut être
engagé.

### Étape 2 — Hôte à parité, dans `src/deck-host/`

Repris **sans modification** depuis `src/streamdeck-ninjatrader/src/` : `utils/visuals.ts`,
`utils/logger.ts` (en retirant le miroir vers `streamDeck.logger`), `services/bridge-client.ts`,
`models/messages.ts`, `actions/status-action.ts`.

À écrire :

- `device.ts` — ouverture, reconnexion, veille et reprise, rendu différentiel par touche, en
  réintroduisant le filtrage que `lastVisualSignature` n'applique pas aujourd'hui. **Épingler la
  police via `fontFiles` et `loadSystemFonts: false` (voir §11) — sans quoi chaque touche coûte
  95 ms au lieu de 2 ms ;**
- `layout.ts` — chargement de `layout.json` (pages × emplacements × action × réglages),
  rechargement à chaud, conscience de la taille de grille (5×3, 8×4, 3×2) ;
- `input.ts` — appui simple, appui long, accord à deux touches ;
- `host.ts` — reprise de `computeVisual` et des 23 gestionnaires de `plugin.ts:261-800`, en
  remplaçant la table `tracked` (indexée par `action.id`) par une indexation par emplacement de
  grille ;
- `supervisor.ts` — démarrage, arrêt et surveillance du bridge, instance unique, icône de barre
  d'état.

`layout.json` initial : transcrire les 15 touches réparties sur 3 pages du profil actif
(`%APPDATA%\Elgato\StreamDeck\ProfilesV3\3ED67DBE-*.sdProfile\manifest.json`).

### Étape 3 — Empaquetage

Installeur Inno Setup : dépôt dans `%LOCALAPPDATA%`, démarrage automatique, désactivation
réversible de la clé `Run` d'Elgato, désinstallation propre. L'application Elgato **reste
installée** pour les mises à jour de firmware.

### Étape 4 — Vérification

- Rejouer intégralement `docs/test-plan.md` (scénarios T-xx) sur l'hôte propriétaire.
- Tests nouveaux : débranchement puis rebranchement USB en position ouverte ; veille et reprise de
  Windows ; mise à mort de l'hôte en position ouverte — le watchdog doit le relancer et redessiner
  intégralement le deck ; mise à mort du bridge — l'hôte doit afficher l'état déconnecté et
  refuser les ordres.
- Vérifier que les trois fichiers journaliers de `%APPDATA%\StreamDeckTrader\logs\` sont toujours
  alimentés.
- Comparer le délai fill → affichage à la mesure de l'étape 0.

### Point de sortie

À l'issue de l'étape 2, décider explicitement : conserver l'hôte propriétaire, ou revenir à
l'application Elgato en réactivant son démarrage automatique, le plugin étant resté en place. Ne
pas engager les étapes 3 et 4 avant cette décision.
