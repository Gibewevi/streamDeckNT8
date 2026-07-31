# CLAUDE.md — Plugin Stream Deck

Contexte spécifique à ce dossier. Voir le `CLAUDE.md` racine pour l'architecture d'ensemble.

## Où se trouve la logique

**Tout est dans `src/plugin.ts`** : enregistrement des actions, calcul des visuels
(`computeVisual`), gestion d'état, corrélation avec le bridge, macro de sécurité, cooldown.

Ces fichiers ne sont **importés nulle part** et n'ont donc aucun effet à l'exécution :
`src/actions/base-action.ts`, `order-actions.ts`, `position-actions.ts`, `qty-actions.ts`,
`instrument-action.ts`, `src/services/display-adapter.ts`. Seul `actions/status-action.ts` est
utilisé, pour `StatusDisplayAction.getDisplayText`. Ne pas y chercher — ni y corriger — un
comportement observé sur le deck.

Fichiers réellement actifs : `plugin.ts`, `services/bridge-client.ts`, `models/messages.ts`,
`utils/visuals.ts`, `utils/logger.ts`, `actions/status-action.ts`.

## Contraintes de l'environnement Elgato

- Le SDK `@elgato/streamdeck` **lit `manifest.json` au moment de l'import** : importer un module
  qui l'importe échoue si le répertoire de travail n'est pas le dossier `.sdPlugin`. Pour exécuter
  du code du plugin hors de Stream Deck, se placer dans
  `src/streamdeck-ninjatrader/com.trader.ninjatrader.sdPlugin/`.
- `manifest.json` n'est relu qu'au **redémarrage de Stream Deck** : une action ajoutée ou renommée
  n'apparaît pas sans ce redémarrage.
- `manifest.json` pointe sur `dist/plugin.js` (`CodePath`) : le déploiement copie `dist/` en
  conservant ses sous-dossiers, les imports étant relatifs (`./services/…`, `./utils/…`).
- Les touches sont rendues en **SVG** (`utils/visuals.ts`) puis poussées par `setImage`. Le titre
  natif est vidé sauf pour le compte à rebours du cooldown. Attention aux caractères à échapper
  dans le SVG : `&` casse le rendu (d'où les libellés qui l'évitent).
- ESM strict (`"type": "module"`, `module: Node16`) : **tout import relatif doit finir par `.js`**,
  y compris depuis un `.ts`.

## Journalisation

`utils/logger.ts` écrit dans le fichier du jour (`%APPDATA%\StreamDeckTrader\logs\plugin-*.log`)
**et** miroite vers `streamDeck.logger`. Utiliser ce module, pas `streamDeck.logger` directement,
sinon l'événement n'atterrit pas dans le fichier durable.

- `log.event/eventWarn/fail` (avec catégorie + contexte) pour tout ce qui compte ;
- `log.traceEvent` obligatoire dans le chemin des mises à jour d'état (2 s) et des
  rafraîchissements de touches, sinon le fichier explose ;
- les visuels ne sont journalisés qu'au changement, via `lastVisualSignature` ;
- `log.installProcessHandlers()` est appelé en tout premier dans `plugin.ts` : sans lui, une
  exception non interceptée tue le processus et Stream Deck le relance en silence, sans trace.

## Commandes

```bash
npm run build      # tsc → dist/
npx tsc --noEmit   # vérification de types seule
npm run watch      # recompilation continue
```

Après un build, le plugin déployé n'est pris en compte qu'au **redémarrage de Stream Deck**
(le processus node en cours garde l'ancien code en mémoire).
