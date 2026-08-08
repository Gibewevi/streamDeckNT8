# TradeDeck et Bitlearn — la carte

À lire **en premier** si vous découvrez ce projet. Il s'étend sur deux dépôts Git sans lien entre
eux, et la première question que tout le monde pose est « où est le moteur ».

## Le moteur n'est pas dans Bitlearn

```
C:\Users\pixel\Desktop\stream deck        ← CE dépôt : le moteur
  src/NinjaTrader.AddOn.StreamDeck        C# .NET 4.8, NinjaScript
                                          Le SEUL composant qui parle à NinjaTrader.
                                          Envoie les ordres, lit positions et exécutions.
  src/StreamDeckBridge                    C# .NET 8
                                          Les règles : Guard, Anti-Tilt, pause obligatoire,
                                          liquidation automatique. Décide ce qui est REFUSÉ.
  src/deck-host                           Node / TypeScript
                                          Le boîtier : dessine les touches, écoute les appuis,
                                          parle au bridge, se synchronise avec Bitlearn.

C:\Users\pixel\Desktop\Bitlearn           ← l'autre dépôt : le site
  app/tradedeck/                          pages produit, appairage, éditeur
  app/components/tradedeck/               l'éditeur de disposition, le journal, la psychologie
  app/api/tradedeck/                      11 routes : appairage, layout, sync, journal…
  lib/tradeDeck/                          calculs purs + code PARTAGÉ (voir plus bas)
```

Le chemin d'un ordre :

```
touche ──► deck-host ──ws://127.0.0.1:8218──► bridge ──► add-on ──► NinjaTrader
```

**Bitlearn n'y figure pas.** Mesuré sur 219 ordres réels : médiane 3 ms. Site injoignable, le deck
continue de trader sur sa disposition en cache. C'est une propriété à ne jamais casser.

## Où modifier quoi

| Ce que vous voulez changer | Dépôt, fichier |
|---|---|
| Ce que fait une touche quand on appuie | `deck-host/src/host.ts` (l'aiguillage) |
| Une règle de protection | `StreamDeckBridge/SafetyMacro.cs` |
| La façon d'envoyer un ordre à NT8 | `NinjaTrader.AddOn.StreamDeck/Services/TradingEngine.cs` |
| L'apparence d'une touche **sur le boîtier** | `deck-host/src/visual-engine.ts` |
| L'éditeur, le journal, une page web | Bitlearn |
| **Une nouvelle macro** | les deux, dans l'ordre ci-dessous |

### Ajouter une macro

**L'hôte d'abord, le catalogue ensuite. Jamais l'inverse** : une entrée au catalogue que le deck
ne sait pas exécuter est une touche morte que rien ne signale, et c'est le trader qui la découvre
en appuyant.

1. `deck-host/src/host.ts` — traduire l'identifiant en commande bridge
2. `NinjaTrader.AddOn.StreamDeck/Services/{CommandDispatcher,TradingEngine}.cs` si la commande
   n'existe pas
3. `StreamDeckBridge/MessageValidator.cs` — autoriser l'action
4. `deck-host/src/visual-engine.ts` — le visuel de la touche
5. `deck-host/src/catalog.ts` — l'entrée
6. `npm run build` dans `deck-host` : Bitlearn reçoit le catalogue tout seul

## Le code partagé est GÉNÉRÉ

`Bitlearn/lib/tradeDeck/deck-core/` contient six fichiers **copiés depuis ce dépôt** par
`src/deck-host/scripts/emit-shared.mjs`, branché dans `npm run build` :

```
catalog.ts  messages.ts  status-display.ts  visuals.ts  visual-engine.ts  layout-model.ts
```

**Ne jamais les éditer côté Bitlearn** — ils portent un en-tête qui le dit, et le prochain build
les écrase sans avertissement. Éditer ici, puis `npm run build`.

Pourquoi ce mécanisme : `npm run build` est le seul chemin vers `dist/host.js`, donc vers le `.exe`.
On ne peut pas expédier une modification du moteur sans que les copies soient réécrites. La
divergence passe de *détectable* à *impossible*. Avant ça, `restingVisual.js` — une réécriture à la
main des mêmes visuels — avait déjà divergé sur onze points : l'aperçu de l'éditeur montrait la
couleur « connecté » sur une touche au repos.

`npm run emit:check` vérifie sans écrire, et refuse aussi tout import de module Node dans
l'ensemble partagé : ces fichiers doivent tourner dans un navigateur.

### Ce qui n'est PAS partagé, exprès

- **`validateLayout` existe en double** (`deck-host/src/layout.ts` et `Bitlearn/lib/tradeDeck/layout.js`).
  Les deux s'appliquent **en série** et ne protègent pas de la même chose : ici d'un fichier local
  corrompu, là-bas d'un document hostile venu d'Internet vers une colonne `Json`. La version
  Bitlearn est plus stricte de onze contrôles. **Ne pas unifier** — c'est écrit aux deux endroits.
- `layout.ts` (dépend de `fs`), `render-node.ts` (dépend de `Buffer` et du SDK Elgato).
- Côté Bitlearn : `psychology.js`, `rollup.js`, `pairing.js`, `icons.js` — sans homologue.

## Le filet de test

Ce dépôt n'a **aucun test**. Bitlearn en a 1228, dont
`lib/tradeDeck/deckPreview.test.js` : un snapshot des 26 visuels de touches, produits par
`visual-engine.ts` de ce dépôt. **Toute modification du dessin d'une touche fait rougir un test
Bitlearn.** C'est la seule couverture du moteur de visuels — la garder verte, ou mettre le
snapshot à jour en connaissance de cause.

Pour le bridge, un harnais jetable pilote la vraie `SafetyMacro` en réécrivant son fichier d'état
(le seul moyen d'avancer l'horloge sans injecter un faux temps dans le code de production). 28
contrôles au 07/08/2026 ; il vit dans le scratchpad, à reconstruire au besoin.

## Documents

| Fichier | Contenu |
|---|---|
| `CLAUDE.md` (ce dépôt) | commandes, déploiement, **pièges à connaître**, conventions |
| `docs/publier-une-version.md` | produire un installateur — à suivre à chaque changement du moteur |
| `docs/architecture.md` | l'architecture d'origine à trois composants, ports, résolution de contexte |
| `docs/protocol.md` | le protocole de messages entre composants |
| `Bitlearn/docs/tradedeck-integration.md` | **le registre du chantier d'intégration** — décisions,
  alternatives écartées, et le raisonnement derrière chaque garde-fou |

## Deux conventions qui surprennent

**Les artefacts de build (`bin/`, `obj/`, `dist/`) sont versionnés dans ce dépôt**, délibérément.
Une modification de source fait apparaître des dizaines de binaires dans `git status` : c'est
attendu. **Ne pas « nettoyer » sans demander** — c'est écrit dans `CLAUDE.md` et dans `.gitignore`.

**Les commentaires expliquent le pourquoi, et devant un garde-fou la raison est presque toujours un
incident de trading réel.** Ne pas les supprimer en refactorisant : ce sont eux qui empêchent de
« simplifier » une protection en la vidant de son sens.
