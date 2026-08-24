# Journal des modifications

## Non publié

- `JournalSeal.cs` passe au namespace neutre `TradeDeck.Journal` pour être partagé avec la stratégie
  ATAS. **Ce fichier est livré chez le trader** : la version installée doit être reconstruite
  (`docs/publier-une-version.md`), sinon `ExecutionRecorder` référence un namespace absent et la
  compilation NinjaScript échoue en bloc, indicateurs du trader compris.
