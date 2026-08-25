# Journal des modifications

## Non publié

- `JournalSeal.cs` passe au namespace neutre `TradeDeck.Journal` pour être partagé avec la stratégie
  ATAS. **Ce fichier est livré chez le trader** : la version installée doit être reconstruite
  (`docs/publier-une-version.md`), sinon `ExecutionRecorder` référence un namespace absent et la
  compilation NinjaScript échoue en bloc, indicateurs du trader compris.
- Chemin d'ordre porté sur ATAS classique : `src/Atas.Strategy.TradeDeck/` exécute les 13 commandes,
  publie l'état toutes les 500 ms, remonte les rejets, applique la macro de sécurité aux ordres
  passés hors du deck et journalise les exécutions scellées. Le bridge et le deck-host sont
  inchangés. La macro Tendance et le copieur multi-comptes ne sont pas portés.
